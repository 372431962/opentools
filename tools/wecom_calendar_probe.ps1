<#
  wecom_calendar_probe.ps1
  ------------------------------------------------------------------
    No-GUI connectivity probe for the account that WeCom shows under
    schedule -> calendar settings -> "sync to other calendars", so we
    can answer BEFORE writing any WPF code:

      0. which protocol is it? EAS (Microsoft-Server-ActiveSync), real CalDAV
         (PROPFIND advertising DAV: calendar-access), or nothing but an edge?
         The RFC 5352 well-known URI is tried too, because a host can route
         only that -- and WeCom does exactly that on the caldav. subdomain.
      1. does HTTP Basic with those credentials authenticate at all?
      2. can we discover principal -> calendar-home -> calendar list?
      3. does REPORT calendar-query with a time range return events?
      4. are all-day events / recurring events / time zones sane, and
         do the credentials stay valid across runs?

    Why step 0 has to be measured and not assumed: WeCom's own sync
    guide documents the account type as "Microsoft Exchange" on iOS and
    "Exchange ActiveSync" on Windows, states the password must be
    fetched again for every use, and says Mac Calendar is unsupported.
    EAS bodies are WBXML (binary), not XML, so a CalDAV client would
    have nothing to talk to if the endpoint turns out to be EAS.

    Where to get the credentials (Chinese UI path is in tools/README.md):
      the page shows three fields -- the server host, the user name and a
      password. The password is masked and may rotate, so paste it fresh
      for each run.

    Nothing is written to disk unless -OutDir is given; the password is
    never echoed.

    Usage (interactive, nothing on the command line):
      powershell -NoProfile -ExecutionPolicy Bypass -File wecom_calendar_probe.ps1
    Also fine:
      ... -Server <host-from-wecom> -User <name> -Days 60 -OutDir C:\se\ics
      ... -User <name> -PasswordFile C:\se\wecom.txt
      ... -Server wecom.work -User probe -NoAuth          # protocol check only
      ... -Server wecom.work -User <name> -Ews            # EWS instead of CalDAV

    -NoAuth is required for an unattended run: the password prompt uses
    Read-Host -AsSecureString, which blocks forever when stdin is not a
    console (CI, "cmd /c", or another process piping into powershell).

    Messages are ASCII on purpose: Windows PowerShell 5.1 reads BOM-less
    .ps1 files using the ANSI codepage.
#>
[CmdletBinding()]
param(
    [string]$Server,                  # paste the server host WeCom shows
    [string]$User,
    [string]$Password,
    [string]$PasswordFile,
    [int]$Days = 45,
    [string]$CalendarFilter,
    [string]$CalendarPath,          # skip discovery and query this path directly
    [switch]$ListOnly,
    [switch]$SkipDetect,
    [switch]$NoAuth,                  # skip the password prompt: the protocol check needs no credentials
    [switch]$Ews,                    # talk Exchange Web Services (SOAP) instead of CalDAV
    [string]$EwsPath = '/EWS/Exchange.asmx',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = `
    [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls

# WeCom shows the server as a bare host (sometimes with a path). Normalise it.
if (-not $Server) { $Server = Read-Host 'Server (the value shown in WeCom, e.g. a host name)' }
$Server = $Server.Trim().TrimEnd('/')
if ($Server -notmatch '^[A-Za-z][A-Za-z0-9+.-]*://') { $Server = 'https://' + $Server }
if (-not [Uri]::IsWellFormedUriString($Server, [UriKind]::Absolute)) {
    Write-Host "  -Server is not a valid URL: '$Server'"; exit 1
}

$script:AuthHeader = $null
$script:LastStatus = $null   # HTTP status of the most recent call, 0 = no response

function Get-Basic([string]$u, [string]$p) {
    # RFC 7617: the user-id and password are UTF-8, so the WeCom Chinese
    # display name (if that is what is used as the user) still encodes.
    return 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($u + ':' + $p)))
}

# Namespace prefixes for XPath. Registering both 'caldav' and 'c' keeps the
# helper usable on responses from servers that use either prefix.
# The leading comma is required: XmlNamespaceManager is IEnumerable, so a bare
# "return $m" would hand back its namespace names instead of the manager.
function New-DavNs([xml]$Xml) {
    $m = New-Object Xml.XmlNamespaceManager($Xml.NameTable)
    $m.AddNamespace('d', 'DAV:')
    $m.AddNamespace('caldav', 'urn:ietf:params:xml:ns:caldav')
    $m.AddNamespace('c', 'urn:ietf:params:xml:ns:caldav')
    $m.AddNamespace('cs', 'http://apple.com/ns/ical/')
    return ,$m
}

# Truncated, single-line preview of a response body, for error messages.
function Summarize([string]$s) {
    $t = ($s -replace '\s+', ' ').Trim()
    if ($t.Length -gt 400) { $t = $t.Substring(0, 400) + '...' }
    return $t
}

# Raw HTTP with custom verbs (PROPFIND / REPORT), which Invoke-WebRequest
# cannot send on PowerShell 5.1.
function Invoke-Caldav {
    param([string]$Method, [string]$Url, [string]$Body, [string]$Depth, [string]$ContentType, [string]$SoapAction)

    $req = [Net.HttpWebRequest]::Create($Url)
    $req.Method = $Method
    $req.Timeout = 30000
    $req.AllowAutoRedirect = $true
    $req.Accept = 'application/xml, text/xml, text/calendar'
    if ($script:AuthHeader) { $req.Headers.Add('Authorization', $script:AuthHeader) }
    if ($Depth) { $req.Headers.Add('Depth', $Depth) }
    if ($SoapAction) { $req.Headers.Add('SOAPAction', $SoapAction) }
    if ($Body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
        $req.ContentType = $(if ($ContentType) { $ContentType } else { 'application/xml; charset=utf-8' })
        $req.ContentLength = $bytes.Length
        $rs = $req.GetRequestStream(); $rs.Write($bytes, 0, $bytes.Length); $rs.Close()
    }

    try {
        $resp = $req.GetResponse()
    } catch [Net.WebException] {
        $r = $_.Exception.Response
        if (-not $r) {
            # DNS failure, refused connection, TLS problem: no HTTP response at all.
            $script:LastStatus = 0
            Write-Host ("  {0} {1} -> {2}" -f $Method, $Url, $_.Exception.Message)
            return $null
        }
        $script:LastStatus = [int]$r.StatusCode
        $body = ''
        try {
            $sr = New-Object IO.StreamReader($r.GetResponseStream(), [Text.Encoding]::UTF8)
            $body = $sr.ReadToEnd(); $sr.Close()
        } catch { }
        Write-Host ("  {0} {1} -> HTTP {2} {3}" -f $Method, $Url, [int]$r.StatusCode, $r.StatusDescription)
        $wa = $r.Headers['WWW-Authenticate']
        if ($wa) { Write-Host "  WWW-Authenticate: $wa" }
        if ($body.Trim()) { Write-Host ("  body: {0}" -f (Summarize $body)) }
        return $null
    }

    $sr2 = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $text = $sr2.ReadToEnd(); $sr2.Close()
    $script:LastStatus = [int]$resp.StatusCode
    $hdrs = @{}
    foreach ($k in $resp.Headers.AllKeys) { $hdrs[$k] = $resp.Headers[$k] }
    $result = [pscustomobject]@{
        Status  = [int]$resp.StatusCode
        Body    = $text
        Uri     = $resp.ResponseUri.AbsoluteUri
        Headers = $hdrs
    }
    $resp.Close()
    return $result
}

# PROPFIND with an explicit property list. Names without a prefix are taken
# to be DAV:, e.g. 'displayname' -> <d:displayname/>, 'caldav:calendar-home-set'
# -> <caldav:calendar-home-set/>.
#   Propfind -Url $u -Props @('current-user-principal','displayname') -Depth '0'
function Propfind {
    param([string]$Url, [string[]]$Props, [string]$Depth = '1')

    $list = ($Props | ForEach-Object { if ($_.Contains(':')) { "<$_/>" } else { "<d:$_/>" } }) -join ''
    $body = @'
<?xml version="1.0" encoding="utf-8"?>
<d:propfind xmlns:d="DAV:" xmlns:caldav="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://apple.com/ns/ical/">
  <d:prop>__PROPS__</d:prop>
</d:propfind>
'@
    $body = $body.Replace('__PROPS__', $list)
    return Invoke-Caldav -Method 'PROPFIND' -Url $Url -Body $body -Depth $Depth
}

function Resolve-Url([string]$href) {
    if ($href -match '^https?://') { return $href }
    return (New-Object Uri([Uri]$Server, $href)).AbsoluteUri
}

# HttpWebRequest only auto-follows redirects for GET/HEAD/POST, so a PROPFIND
# that comes back 3xx -- which is how RFC 5352 well-known URIs point at the
# principal -- has to be followed by hand. Returns the absolute target, or $null.
function Get-RedirectTarget([object]$Result) {
    if (-not $Result) { return $null }
    if ($Result.Status -lt 300 -or $Result.Status -ge 400) { return $null }
    $loc = $Result.Headers['Location']
    if (-not $loc) {
        Write-Host "    HTTP $($Result.Status) with no Location -- nothing to follow"
        return $null
    }
    $target = Resolve-Url $loc
    Write-Host "    redirected (HTTP $($Result.Status)) -> $target"
    return $target
}

Write-Host "== 0) credentials =="
if (-not $User) { $User = Read-Host 'CalDAV user (as shown in WeCom)' }
if (-not $Password -and $PasswordFile) {
    if (Test-Path -LiteralPath $PasswordFile) {
        $Password = (Get-Content -LiteralPath $PasswordFile -TotalCount 1).Trim()
        Write-Host "  password read from $PasswordFile"
    } else {
        Write-Host "  -PasswordFile '$PasswordFile' does not exist"
    }
}
if (-not $Password -and -not $NoAuth) {
    # Read-Host -AsSecureString needs a real console: under redirected stdin it
    # blocks forever, so an unattended run must pass -NoAuth.
    $secure = Read-Host 'CalDAV password (empty = unauthenticated probe)' -AsSecureString
    if ($secure.Length -gt 0) {
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure)
        try { $Password = [Runtime.InteropServices.Marshal]::PtrToStringUni($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($ptr) }
    }
}
if ($Password) {
    $script:AuthHeader = Get-Basic $User $Password
} else {
    Write-Host '  no password: running unauthenticated (discovery and EWS will 401)'
}
Write-Host "  server=$Server user=$User auth=$(if ($script:AuthHeader) { 'yes' } else { 'no' })"

Write-Host "== 1) what does this endpoint actually speak? =="
$easUrl = "$Server/Microsoft-Server-ActiveSync"
$eas = $null
$easStatus = 0
if (-not $SkipDetect) {
    $eas = Invoke-Caldav -Method 'OPTIONS' -Url $easUrl
    $easStatus = [int]$script:LastStatus
    if ($eas) {
        Write-Host "  EAS   HTTP $($eas.Status)"
        foreach ($h in @('Allow', 'MS-ASHTTP', 'MS-Server-Exchange-ActiveSync', 'MS-ASXML')) {
            if ($eas.Headers[$h]) { Write-Host ("    {0}: {1}" -f $h, $eas.Headers[$h]) }
        }
    }
}
$opt = Invoke-Caldav -Method 'OPTIONS' -Url "$Server/"
$rootStatus = [int]$script:LastStatus
$isCaldav = $false
if ($opt) {
    Write-Host "  CalDAV-side OPTIONS / -> HTTP $($opt.Status)"
    foreach ($h in @('Allow', 'DAV', 'MS-Author-Via', 'Server')) {
        if ($opt.Headers[$h]) { Write-Host ("    {0}: {1}" -f $h, $opt.Headers[$h]) }
    }
    $isCaldav = ($opt.Headers['DAV'] -match 'calendar-access')
}
# EWS lives on the same host as EAS but speaks a different protocol, and unlike
# EAS it needs no device provisioning, so its presence is worth recording even
# when this run is not going to send SOAP.
$ewsStatus = 0
if (-not $SkipDetect -and -not $Ews) {
    $ewsOpt = Invoke-Caldav -Method 'OPTIONS' -Url "$Server$EwsPath"
    $ewsStatus = [int]$script:LastStatus
    if ($ewsOpt) { Write-Host "  EWS   OPTIONS $EwsPath -> HTTP $($ewsOpt.Status)" }
}
# RFC 5352: a host can route only the well-known URIs, so a 403 on / proves
# nothing until these have been tried. A 401 here means the CalDAV entry point
# exists and the password will actually be evaluated.
$wkUrl = "$Server/.well-known/caldav"
$wkStatus = 0
if (-not $SkipDetect) {
    $wk = Invoke-Caldav -Method 'OPTIONS' -Url $wkUrl
    $wkStatus = [int]$script:LastStatus
    if ($wk) {
        Write-Host "  CalDAV-side OPTIONS /.well-known/caldav -> HTTP $($wk.Status)"
        if ($wk.Headers['DAV']) { Write-Host ("    DAV: {0}" -f $wk.Headers['DAV']) }
        $isCaldav = $isCaldav -or ($wk.Headers['DAV'] -match 'calendar-access')
    }
}
# Where discovery starts: prefer the path that actually answered.
$script:DavBase = if ($opt) { "$Server/" } else { $wkUrl }
if ($easStatus -eq 401 -or $ewsStatus -eq 401) {
    Write-Host '  The EAS/EWS paths answer 401 with a Basic challenge while PROPFIND on every CalDAV'
    Write-Host '  path 403s: the account service is reachable, DAV is not served here.'
    Write-Host '  But an OPTIONS 401 only proves the edge asks for a password -- on WeCom a POST without'
    Write-Host '  a Cmd= parameter 502s -- so "which protocol is behind it" stays open until a'
    Write-Host '  credentialed call answers. Next step: -Ews with the password pasted, which sends SOAP.'
}
if ($wkStatus -eq 401) {
    Write-Host "  $wkUrl challenges with Basic, i.e. this host does route the well-known DAV"
    Write-Host '  entry point and will check the password. Discovery below starts there. If the WeCom'
    Write-Host '  page shows a bare domain and every path 403s, also try the caldav. subdomain: on'
    Write-Host '  WeCom the apex 403s these same URIs while caldav.<domain> 401s them.'
}
# Only bail out when nothing challenged us at all: a 401 from EAS, EWS or the
# well-known DAV URI means the host does speak something, and the caller still
# wants to see what a PROPFIND gets.
if (-not $SkipDetect -and -not $Ews -and -not $opt -and -not $eas `
    -and $easStatus -ne 401 -and $ewsStatus -ne 401 -and $wkStatus -ne 401) {
    switch ($rootStatus) {
        0 { Write-Host '  VERDICT: no HTTP response at all (DNS, TLS or connection refused).' }
        403 {
            Write-Host '  VERDICT: 403 with no WWW-Authenticate challenge -- an edge (WAF, IP'
            Write-Host '    allow-list, or an unknown path) is refusing us BEFORE asking for a'
            Write-Host '    password. This is not a credential problem: re-check the server string'
            Write-Host '    WeCom shows, or run this from the network the account allows.'
        }
        401 { Write-Host '  VERDICT: challenged (401) but the credentials were rejected.' }
        default { Write-Host ("  VERDICT: both OPTIONS calls failed (last HTTP status {0})." -f $rootStatus) }
    }
    exit 2
}
if (-not $isCaldav -and $eas) {
    Write-Host '  VERDICT: this is Exchange ActiveSync, not CalDAV.'
    Write-Host '    Bodies are WBXML, the device usually has to be provisioned, and a third-party'
    Write-Host '    client may be refused by policy. Do NOT build the WPF CalDAV client on this.'
    Write-Host '    Continuing anyway so you can see what a PROPFIND does to it.'
}
if (-not $isCaldav) {
    Write-Host '  note: no "DAV: calendar-access" header yet. Some servers only advertise it on'
    Write-Host '        the calendar collection, so discovery below is what settles it.'
}

# ---------------------------------------------------------------------------
# Exchange Web Services branch. WeCom's sync account is documented as Exchange
# / Exchange ActiveSync, and the host really does expose /EWS/Exchange.asmx, so
# EWS -- plain XML over HTTP Basic -- is the protocol worth trying first. EAS
# would need WBXML plus device provisioning, which a widget should not do.
# ---------------------------------------------------------------------------
function New-EwsNs([xml]$Xml) {
    $m = New-Object Xml.XmlNamespaceManager($Xml.NameTable)
    $m.AddNamespace('s', 'http://schemas.xmlsoap.org/soap/envelope/')
    $m.AddNamespace('m', 'http://schemas.microsoft.com/exchange/services/2006/messages')
    $m.AddNamespace('t', 'http://schemas.microsoft.com/exchange/services/2006/types')
    return ,$m
}

function Invoke-Ews {
    param([string]$Action, [string]$Envelope)
    $ns = "http://schemas.microsoft.com/exchange/services/2006/messages/$Action"
    return Invoke-Caldav -Method 'POST' -Url "$Server$EwsPath" -Body $Envelope `
        -ContentType 'text/xml; charset=utf-8' -SoapAction "`"$ns`""
}

function Show-EwsResult {
    param([string]$Label, [object]$Result, [string]$MessageXPath)
    if (-not $Result) { Write-Host "  $Label -> no usable response (see the status above)"; return $null }
    $xml = $null
    try { $xml = [xml]$Result.Body } catch {
        Write-Host "  $Label -> HTTP $($Result.Status) but the body is not XML: $(Summarize $Result.Body)"
        return $null
    }
    $ns = New-EwsNs $xml
    $fault = $xml.SelectSingleNode('//s:Fault', $ns)
    if ($fault) {
        $text = ($fault.OuterXml -replace '\s+', ' ')
        Write-Host "  $Label -> SOAP FAULT: $(Summarize $text)"
        return $null
    }
    $code = $xml.SelectSingleNode("//$MessageXPath/m:ResponseCode", $ns)
    $cls = $xml.SelectSingleNode("//$MessageXPath", $ns)
    $responseClass = if ($cls) { $cls.ResponseClass } else { '?' }
    Write-Host "  $Label -> HTTP $($Result.Status), ResponseClass=$responseClass, ResponseCode=$(if ($code) { $code.InnerText } else { '?' })"
    return @{ Xml = $xml; Ns = $ns }
}

if ($Ews) {
    if (-not $script:AuthHeader) { Write-Host '  -Ews without a password: expect 401 (challenged) or 502 (nothing routed)' }

    # Separates "the password is wrong" from "EWS is not there", which the two
    # SOAP calls below cannot tell apart on their own. A POST without Cmd= is
    # refused by routing (502), so the command parameter has to be present.
    Write-Host '== E0) do these credentials authenticate at all? (EAS POST ?Cmd=Options) =='
    if ($script:AuthHeader) {
        $easProbe = Invoke-Caldav -Method 'POST' -Url ($easUrl + '?Cmd=Options')
        if ($easProbe) {
            Write-Host "  ACCEPTED (HTTP $($easProbe.Status)) -- so a 401 below is EWS refusing, not the password"
        } else {
            Write-Host '  REFUSED -- the password is wrong, expired or already used. Re-paste it from the'
            Write-Host '  WeCom sync page before reading anything into the EWS results below.'
        }
    }

    Write-Host "== E1) EWS GetFolder (distinguished calendar) =="
    $getFolder = @'
<?xml version="1.0" encoding="utf-8"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Header>
    <h:RequestServerVersion xmlns:h="http://schemas.microsoft.com/exchange/services/2006/messages" Version="Exchange2010_SP2"/>
  </s:Header>
  <s:Body>
    <m:GetFolder xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
      <m:FolderShape>
        <t:BaseShape>IdOnly</t:BaseShape>
        <t:AdditionalProperties>
          <t:FieldURI FieldURI="folder:DisplayName"/>
          <t:FieldURI FieldURI="folder:TotalItemsInView"/>
        </t:AdditionalProperties>
      </m:FolderShape>
      <m:FolderIds><t:DistinguishedFolderId Id="calendar"/></m:FolderIds>
    </m:GetFolder>
  </s:Body>
</s:Envelope>
'@
    $r1 = Show-EwsResult -Label 'GetFolder' -Result (Invoke-Ews -Action 'GetFolder' -Envelope $getFolder) `
        -MessageXPath '//m:GetFolderResponse/m:ResponseMessages/m:GetFolderResponseMessage'
    if ($r1) {
        $folder = $r1.Xml.SelectSingleNode('//t:CalendarFolder', $r1.Ns)
        if (-not $folder) { $folder = $r1.Xml.SelectSingleNode('//m:RootFolder', $r1.Ns) }
        if ($folder) {
            $dn = $folder.SelectSingleNode('t:DisplayName', $r1.Ns)
            Write-Host ("    calendar folder: name='{0}' itemId={1}" -f `
                $(if ($dn) { $dn.InnerText } else { '(none)' }), $folder.ItemId.Id.Substring(0, [Math]::Min(40, $folder.ItemId.Id.Length)))
        } else { Write-Host '    no calendar folder element in the response' }
    }

    $iso = { param($d) $d.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    $start = (Get-Date).Date
    $end = $start.AddDays($Days)
    Write-Host "== E2) EWS GetCalendarView $(& $iso $start) .. $(& $iso $end) =="
    $view = @'
<?xml version="1.0" encoding="utf-8"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Header>
    <h:RequestServerVersion xmlns:h="http://schemas.microsoft.com/exchange/services/2006/messages" Version="Exchange2010_SP2"/>
  </s:Header>
  <s:Body>
    <m:GetCalendarView xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
        StartDate="__START__" EndDate="__END__" MaxEntriesInView="100">
      <m:FolderShape>
        <t:BaseShape>IdOnly</t:BaseShape>
        <t:AdditionalProperties>
          <t:FieldURI FieldURI="calendar:Subject"/>
          <t:FieldURI FieldURI="calendar:Start"/>
          <t:FieldURI FieldURI="calendar:End"/>
          <t:FieldURI FieldURI="calendar:AllDayEvent"/>
          <t:FieldURI FieldURI="calendar:Location"/>
          <t:FieldURI FieldURI="calendar:Recurrence"/>
          <t:FieldURI FieldURI="calendar:Organizer"/>
          <t:FieldURI FieldURI="calendar:RequiredAttendees"/>
        </t:AdditionalProperties>
      </m:FolderShape>
      <m:FolderIds><t:DistinguishedFolderId Id="calendar"/></m:FolderIds>
    </m:GetCalendarView>
  </s:Body>
</s:Envelope>
'@
    $view = $view.Replace('__START__', (& $iso $start)).Replace('__END__', (& $iso $end))
    $r2 = Show-EwsResult -Label 'GetCalendarView' -Result (Invoke-Ews -Action 'GetCalendarView' -Envelope $view) `
        -MessageXPath '//m:GetCalendarViewResponse/m:ResponseMessages/m:GetCalendarViewResponseMessage'
    if ($r2) {
        $root = $r2.Xml.SelectSingleNode('//m:RootFolder', $r2.Ns)
        if ($root) {
            Write-Host ("    TotalItemsInView={0} IncludesLastItemInRange={1}" -f $root.TotalItemsInView, $root.IncludesLastItemInRange)
        }
        $items = $r2.Xml.SelectNodes('//m:RootFolder/t:CalendarItem', $r2.Ns)
        if (-not $items -or $items.Count -eq 0) { $items = $r2.Xml.SelectNodes('//t:CalendarItem', $r2.Ns) }
        $shown = 0; $allDay = 0; $recur = 0; $withAttendees = 0
        foreach ($item in $items) {
            $shown++
            $get = { param($name) $n = $item.SelectSingleNode("t:$name", $r2.Ns); if ($n) { $n.InnerText } else { '' } }
            $subject = & $get 'Subject'
            $startText = & $get 'Start'
            $endText = & $get 'End'
            if ((& $get 'AllDayEvent') -eq 'true') { $allDay++ }
            if ($item.SelectSingleNode('t:Recurrence', $r2.Ns)) { $recur++ }
            $attendees = $item.SelectNodes('t:RequiredAttendees/t:Attendee', $r2.Ns)
            if ($attendees -and $attendees.Count -gt 0) { $withAttendees++ }
            if ($shown -le 15) {
                Write-Host ("    [{0}] start={1,-21} end={2,-21} allday={3,-5} recur={4,-5} attendees={5}" -f `
                    $shown, $startText, $endText, (& $get 'AllDayEvent'), $(if ($item.SelectSingleNode('t:Recurrence', $r2.Ns)) { 'yes' } else { 'no' }), $(if ($attendees) { $attendees.Count } else { 0 }))
                Write-Host "         subject=$subject  location=$(& $get 'Location')  organizer=$(& $get 'Organizer')"
            }
        }
        Write-Host "    items=$shown allDay=$allDay recurring=$recur withAttendees=$withAttendees"
        if ($shown -eq 0) { Write-Host '    no items in range -- auth and folder worked, the range is just empty' }
    }

    Write-Host '== E3) what to decide from this =='
    Write-Host '  - GetFolder Success + items       -> build the WPF reader on EWS, no CalDAV needed'
    Write-Host '  - HTTP 502 with no challenge      -> nothing is routed behind that path here. Before'
    Write-Host '                                     giving up, re-run -Ews against the caldav. subdomain:'
    Write-Host '                                     WeCom routes the two hosts differently.'
    Write-Host '  - 401 on both                     -> these credentials are EAS-only or they rotated'
    Write-Host '  - GetFolder Success, view empty   -> check -Days; the calendar may really be empty'
    Write-Host '  - ResponseClass=Error             -> read ResponseCode: ErrorAccessDenied means the'
    Write-Host '    mailbox policy blocks EWS even though the endpoint answers'
    Write-Host '  - recurring/attendee columns      -> decide how day cells expand series and who to show'
    exit 0
}

$homeUrl = $null
if (-not $CalendarPath) {
    Write-Host "== 2) current-user-principal (from $script:DavBase) =="
    $principalUrl = $null
    $pf = Propfind -Url $script:DavBase -Depth '0' -Props @('current-user-principal', 'displayname', 'principal-URL')
    if ($pf) {
        $xml = [xml]$pf.Body
        $ns = New-DavNs $xml
        $p = $xml.SelectSingleNode('//d:current-user-principal/d:href', $ns)
        if (-not $p) { $p = $xml.SelectSingleNode('//d:principal-URL/d:href', $ns) }
        if ($p) { $principalUrl = Resolve-Url $p.InnerText }
    }
    Write-Host "  principal = $(if ($principalUrl) { $principalUrl } else { 'not advertised' })"

    Write-Host "== 3) calendar-home-set =="
    if ($principalUrl) {
        $pf2 = Propfind -Url $principalUrl -Depth '0' -Props @('caldav:calendar-home-set', 'displayname')
        if ($pf2) {
            $xml2 = [xml]$pf2.Body
            $ns2 = New-DavNs $xml2
            $homeNode = $xml2.SelectSingleNode('//caldav:calendar-home-set/d:href', $ns2)
            if ($homeNode) { $homeUrl = Resolve-Url $homeNode.InnerText }
        }
    }
    if (-not $homeUrl) {
        Write-Host '  calendar-home not advertised; falling back to depth-1 PROPFIND on the server root'
        $homeUrl = "$Server/"
    }
    Write-Host "  calendar-home = $homeUrl"

    Write-Host "== 4) calendars =="
    $pf3 = Propfind -Url $homeUrl -Depth '1' `
        -Props @('displayname', 'resourcetype', 'getctag', 'caldav:calendar-description', 'caldav:supported-calendar-component-set')
    if (-not $pf3) { Write-Host '  FAILED to list calendars'; exit 1 }
    $xml3 = [xml]$pf3.Body
    $ns3 = New-DavNs $xml3
    $script:Calendars = @()
    foreach ($resp in $xml3.SelectNodes('//d:response', $ns3)) {
        $href = $resp.SelectSingleNode('d:href', $ns3)
        if (-not $href) { continue }
        $url = Resolve-Url $href.InnerText
        $node = $resp.SelectSingleNode('d:propstat/d:prop/d:displayname', $ns3)
        $name = if ($node) { $node.InnerText } else { $null }
        $rt = $resp.SelectSingleNode('d:propstat/d:prop/d:resourcetype', $ns3)
        if (-not ($rt -and $rt.OuterXml -match 'collection')) { continue }
        if ($CalendarFilter -and (($name + ' ' + $url) -notlike "*$CalendarFilter*")) { continue }
        $label = if ($name) { $name } else { '(no displayname)' }
        $script:Calendars += [pscustomobject]@{ Url = $url; Name = $label }
        Write-Host ("  calendar: {0,-28} {1}" -f $label, $url)
    }
    Write-Host "  total: $($script:Calendars.Count)"
    if ($ListOnly) { Write-Host '  (-ListOnly: stopping before the event query)'; exit 0 }
    if ($script:Calendars.Count -eq 0) { Write-Host '  no calendars found; nothing to query'; exit 1 }
} else {
    $script:Calendars = @([pscustomobject]@{ Url = (Resolve-Url $CalendarPath); Name = '(manual -CalendarPath)' })
}

$fmt = { param($d) $d.ToUniversalTime().ToString('yyyyMMddTHHmmssZ') }
$start = (Get-Date).Date
$end = $start.AddDays($Days)
$s = & $fmt $start
$e = & $fmt $end
$query = @'
<?xml version="1.0" encoding="utf-8"?>
<C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
  <D:prop><D:getetag/><C:calendar-data/></D:prop>
  <C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VEVENT">
    <C:time-range start="__START__" end="__END__"/>
  </C:comp-filter></C:comp-filter></C:filter>
</C:calendar-query>
'@
$query = $query.Replace('__START__', $s).Replace('__END__', $e)

if ($OutDir -and -not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

Write-Host "== 5) events in $s .. $e =="
foreach ($cal in $script:Calendars) {
    Write-Host "  --- $($cal.Name) ---"
    $r = Invoke-Caldav -Method 'REPORT' -Url $cal.Url -Body $query -Depth '1'
    if (-not $r) { Write-Host '    REPORT failed'; continue }
    $xml = [xml]$r.Body
    $ns = New-DavNs $xml
    $n = 0; $allDay = 0; $recur = 0; $withTz = 0; $floating = 0
    foreach ($resp in $xml.SelectNodes('//d:response', $ns)) {
        $hrefNode = $resp.SelectSingleNode('d:href', $ns)
        $href = if ($hrefNode) { $hrefNode.InnerText } else { '(no href)' }
        $dataNode = $resp.SelectSingleNode('d:propstat/d:prop/caldav:calendar-data', $ns)
        $data = if ($dataNode) { $dataNode.InnerText } else { $null }
        if (-not $data) { Write-Host "    (no calendar-data for $href)"; continue }
        # RFC 5545 3.1: a fold is CRLF + one space/tab, so unfolding deletes both.
        # Keeping the break would truncate folded SUMMARY/DESCRIPTION values.
        $ics = $data -replace '(\r?\n)[ \t]', ''
        $n++
        if ($OutDir) {
            $safe = ($href -replace '[^\w.-]', '_')
            Set-Content -LiteralPath (Join-Path $OutDir "$($n)_$safe.ics") -Value $ics -Encoding UTF8
        }
        $dtstart = ([regex]'DTSTART[^:]*:(\S+)').Match($ics).Groups[1].Value
        $summary = ([regex]'(?m)^SUMMARY:(.*)$').Match($ics).Groups[1].Value.Trim()
        $rrule = ([regex]'(?m)^RRULE:(.*)$').Match($ics).Groups[1].Value.Trim()
        $tzid = ([regex]'DTSTART;TZID=([^:]+)').Match($ics).Groups[1].Value
        if ($dtstart -match '^\d{8}$') { $allDay++ }
        if ($rrule) { $recur++ }
        if ($tzid) { $withTz++ } elseif ($dtstart -match '^\d{8}T\d{6}$') { $floating++ }
        Write-Host ("    [{0}] {1,-34} start={2,-16} tz={3,-24} rrule={4}" -f `
            $n, $summary, $dtstart, $(if ($tzid) { $tzid } { '-' }), $(if ($rrule) { 'yes' } { '-' }))
        Write-Host "         href=$href"
    }
    Write-Host "    events=$n allDay=$allDay recurring=$recur withTzid=$withTz floating=$floating"
    if ($n -eq 0) { Write-Host '    no events in range (or REPORT ignored the filter)' }
}

Write-Host '== 6) what to decide from this =='
Write-Host '  - auth ok + calendars listed      -> WPF CalDAV client is worth building'
Write-Host '  - 401 on step 2 but OPTIONS alive -> credentials wrong/expired, server is fine'
Write-Host '  - re-run needs a fresh password   -> store a re-paste prompt, not a saved secret'
Write-Host '  - allDay / rrule / TZID columns   -> decide how day cells and recurrences expand'
Write-Host '  - floating=... non-zero           -> server omits TZ; must assume Asia/Shanghai'
Write-Host '  - REPORT empty while events exist -> server ignores time-range; must page the whole calendar'
Write-Host '  - run this twice in a row: if the 2nd run 401s, the password rotates per session'
