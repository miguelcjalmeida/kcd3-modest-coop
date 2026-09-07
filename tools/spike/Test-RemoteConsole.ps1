<#
.SYNOPSIS
  Phase 0 spike: verify KCD2's RemoteConsole (retail, launched with -devmode)
  is reachable and executes arbitrary Lua via the '#' prefix.

.DESCRIPTION
  Wire format (verified against retail 1.5.6 by the reference project this
  plan takes techniques from, re-verified independently here):
    <ascii-digit type><utf8 payload><0x00>
    type '5' = ConsoleCommand

  So to run Lua: send the byte '5', then "#<lua code>", then a single 0x00.

.EXAMPLE
  .\Test-RemoteConsole.ps1
  # sends #System.LogAlways('SPIKE-OK') - check kcd.log afterwards

.EXAMPLE
  .\Test-RemoteConsole.ps1 -Lua "System.LogAlways('hello ' .. tostring(1+1))"
#>

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 4600,
    [string]$Lua = "System.LogAlways('SPIKE-OK')"
)

$ErrorActionPreference = "Stop"

Write-Host "Connecting to RemoteConsole at ${HostName}:${Port} ..."
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($HostName, $Port)
$stream = $client.GetStream()

$payload = "#$Lua"
$bytes = New-Object System.Collections.Generic.List[byte]
$bytes.Add([byte][char]'5')
$bytes.AddRange([System.Text.Encoding]::UTF8.GetBytes($payload))
$bytes.Add(0)

$buf = $bytes.ToArray()
$stream.Write($buf, 0, $buf.Length)
$stream.Flush()

Write-Host "Sent: 5#$Lua`0"
Write-Host "Reading any immediate reply (banner/autocomplete frames only - console-command output goes to kcd.log, not back over RC)..."

Start-Sleep -Milliseconds 300
if ($stream.DataAvailable) {
    $readBuf = New-Object byte[] 4096
    $n = $stream.Read($readBuf, 0, $readBuf.Length)
    Write-Host "Reply ($n bytes): $([System.Text.Encoding]::UTF8.GetString($readBuf, 0, $n))"
} else {
    Write-Host "(no immediate reply - expected for a plain console command)"
}

$stream.Close()
$client.Close()

Write-Host ""
Write-Host "Now check kcd.log for the line you sent, e.g.:"
Write-Host "  Select-String -Path '<KCD2 install>\kcd.log' -Pattern 'SPIKE-OK'"
