#!/usr/bin/env bash
# 直接用 Roslyn 编译（可选运行）项目：本机沙箱里 NuGet 还原不可用，dotnet build 走不通。
# 生产工程按目录扫描 .cs 并带上 XAML 生成的局部类；测试工程按 csproj 的 <Compile Include> 取文件。
# 用法：bash tools/roslyn-check.sh <项目目录> [run]
set -u
PROJECT="${1:?需要项目目录，例如 DesktopCalendarWidget 或 tests/DesktopCalendarWidget.ScheduleTests}"
DO_RUN="${2:-}"

ROOT_POSIX="$(cd "$(dirname "$0")/.." && pwd)"
ROOT_WIN="$(cd "$ROOT_POSIX" && pwd -W)"
PROJ_POSIX="$ROOT_POSIX/$PROJECT"
PROJ_WIN="$ROOT_WIN/$PROJECT"
CSPROJ="$(find "$PROJ_POSIX" -maxdepth 1 -name '*.csproj' | head -1)"
[ -n "$CSPROJ" ] || { echo "找不到 csproj: $PROJECT"; exit 2; }
CSPROJ_DIR_POSIX="$(dirname "$CSPROJ")"
CSPROJ_DIR_WIN="$(cd "$CSPROJ_DIR_POSIX" && pwd -W)"

CORE="C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.31/ref/net8.0"
DESKTOP="C:/Program Files/dotnet/packs/Microsoft.WindowsDesktop.App.Ref/8.0.31/ref/net8.0"
ANALYZERS="C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.31/analyzers/dotnet/cs"
CSC="C:/Program Files/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"
OUT_POSIX="$PROJ_POSIX/obj/roslyn-check"
OUT_WIN="$PROJ_WIN/obj/roslyn-check"
mkdir -p "$OUT_POSIX"

to_win() { printf '%s' "$1" | sed "s|^$ROOT_POSIX|$ROOT_WIN|"; }

# ---- 引用程序集 ----
# Core 的 ref 包里有一个同名 WindowsBase.dll 占位件，它排在前面会让真正的 WPF 类型解析不到
# （CS7069：声称定义在 WindowsBase 却找不到），因此与 Desktop 重名的文件一律以 Desktop 为准。
REFS=()
IS_WPF=0
if ls "$PROJ_POSIX"/*.xaml >/dev/null 2>&1; then IS_WPF=1; fi
for dll in "$CORE"/*.dll; do
  if [ "$IS_WPF" = 1 ] && [ -f "$DESKTOP/$(basename "$dll")" ]; then continue; fi
  REFS+=("-r:$dll")
done
if [ "$IS_WPF" = 1 ]; then
  for dll in "$DESKTOP"/*.dll; do REFS+=("-r:$dll"); done
fi

# ---- 源文件 ----
SOURCES=()
add_source() {
  local candidate="$1"
  local existing
  for existing in ${SOURCES[@]+"${SOURCES[@]}"}; do
    [ "$existing" = "$candidate" ] && return
  done
  SOURCES+=("$candidate")
}

# 项目自身目录下的 .cs（SDK 默认 glob 的那部分）
while IFS= read -r f; do add_source "$(to_win "$f")"; done < <(find "$CSPROJ_DIR_POSIX" -maxdepth 3 -name '*.cs' \
  -not -path '*/obj/*' -not -path '*/bin/*' -not -name '*_wpftmp*')

# csproj 里显式列出的文件（测试工程链接的生产源码在 ../DesktopCalendarWidget/ 下）
COMPILE_LIST="$(tr '\n' ' ' < "$CSPROJ" | grep -o '<Compile Include="[^"]*"' || true)"
if [ -n "$COMPILE_LIST" ]; then
  while IFS= read -r inc; do
    [ -z "$inc" ] && continue
    add_source "$(cd "$CSPROJ_DIR_POSIX/$(dirname "$inc")" && printf '%s/%s' "$(pwd -W)" "$(basename "$inc")")"
  done < <(printf '%s\n' "$COMPILE_LIST" | sed -n 's/.*Include="\([^"]*\)".*/\1/p')
fi

if [ "$IS_WPF" = 1 ]; then
  for g in App.g.cs MainWindow.g.cs SettingsWindow.g.cs TranslateWindow.g.cs; do
    [ -f "$PROJ_POSIX/obj/Release/net8.0-windows/$g" ] && add_source "$PROJ_WIN/obj/Release/net8.0-windows/$g"
  done
fi

# ImplicitUsings 生成的全局 using 文件必须带上，否则 DateTime/IEnumerable 这些都会找不到。
GU="$(find "$PROJ_POSIX/obj" -name '*.GlobalUsings.g.cs' 2>/dev/null | head -1)"
[ -n "$GU" ] && SOURCES+=("$(to_win "$GU")")

# 测试工程链接生产源码时会读程序集版本号，这里补一份与 csproj <Version> 一致的声明。
PROD_CSPROJ="$ROOT_POSIX/DesktopCalendarWidget/DesktopCalendarWidget.csproj"
PROD_VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$PROD_CSPROJ" 2>/dev/null | head -1)"
if [ "$IS_WPF" = 0 ] && [ -n "$PROD_VERSION" ]; then
  printf '[assembly: System.Reflection.AssemblyInformationalVersion("%s")]\n' "$PROD_VERSION" > "$OUT_POSIX/VersionInfo.cs"
  add_source "$OUT_WIN/VersionInfo.cs"
fi

# ---- 嵌入资源（离线词典） ----
RESOURCES=()
FLAT="$(tr '\n' ' ' < "$CSPROJ")"
while IFS= read -r tag; do
  [ -z "$tag" ] && continue
  inc="$(printf '%s' "$tag" | sed -n 's/.*Include="\([^"]*\)".*/\1/p')"
  logical="$(printf '%s' "$tag" | sed -n 's/.*LogicalName="\([^"]*\)".*/\1/p')"
  [ -z "$inc" ] && continue
  abs="$(cd "$CSPROJ_DIR_POSIX/$(dirname "$inc")" && printf '%s/%s' "$(pwd -W)" "$(basename "$inc")")"
  RESOURCES+=("-resource:$abs${logical:+,$logical}")
done < <(printf '%s' "$FLAT" | grep -o '<EmbeddedResource[^>]*>')

TARGET=exe
[ "$IS_WPF" = 1 ] && TARGET=winexe

echo "diag: is_wpf=$IS_WPF refs=${#REFS[@]} sources=${#SOURCES[@]} resources=${#RESOURCES[@]} target=$TARGET"
printf '%s\n' "${SOURCES[@]}" | grep -vc '\.cs$' | sed 's/^/diag: non-cs sources = /'

# 210 个引用直接拼在命令行上会超过 Windows 的长度上限并被截断（末尾的 WindowsBase 就丢了），
# 因此统一走响应文件。
RSP="$OUT_POSIX/args.rsp"
{
  echo "-nologo"

  echo "-nostdlib"
  echo "-target:$TARGET"
  echo "-nullable:enable"
  echo "-langversion:latest"
  echo "-define:NET;NET8_0;NET8_0_OR_GREATER;WINDOWS;WINDOWS8_0_OR_GREATER"
  echo "-optimize+"
  echo "-deterministic"
  echo "-analyzer:\"$ANALYZERS/System.Text.RegularExpressions.Generator.dll\""
  printf '%s\n' "${REFS[@]}" | sed 's/^/"/; s/$/"/'
  printf '%s\n' "${SOURCES[@]}" | sed 's/^/"/; s/$/"/'
  [ "${#RESOURCES[@]}" -gt 0 ] && printf '%s\n' "${RESOURCES[@]}" | sed 's/^/"/; s/$/"/'
  echo "-out:\"$OUT_WIN/check.dll\""
} > "$RSP"

dotnet exec "$CSC" "@$OUT_WIN/args.rsp" > "$OUT_POSIX/log.txt" 2>&1
STATUS=$?
echo "compile exit=$STATUS sources=${#SOURCES[@]} resources=${#RESOURCES[@]}"
sed 's/\r$//' "$OUT_POSIX/log.txt" | grep -E 'error|warning' | head -25

if [ "$STATUS" = 0 ] && [ "$DO_RUN" = "run" ]; then
  cat > "$OUT_POSIX/check.runtimeconfig.json" <<'JSON'
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" }
  }
}
JSON
  echo "---- run ----"
  dotnet exec "$OUT_WIN/check.dll" 2>&1 | tail -40
  echo "run exit=$?"
fi
