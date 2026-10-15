# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/ModeGuard.Tests.ps1   (Pester 3+; no Docker needed, rows are injected)
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\ModeGuard.ps1')

Describe 'Assert-ModeAllowed' {
    $dev = @('postgres|running', 'host|running', 'web|running')
    $prod = @('postgres|running', 'app|running', 'objectstore-prod|running')

    It 'refuses Production while Development host/web run' {
        { Assert-ModeAllowed $dev 'Production' } | Should Throw 'Development is running'
    }
    It 'refuses Development while the Production app runs' {
        { Assert-ModeAllowed $prod 'Development' } | Should Throw 'Production is running'
    }
    It 'refuses Development while only the tunnel runs' {
        { Assert-ModeAllowed @('postgres|running', 'cloudflared|running') 'Development' } | Should Throw 'Production is running'
    }
    It 'refuses when Development is restarting' {
        { Assert-ModeAllowed @('web|restarting') 'Production' } | Should Throw 'Development is running'
    }
    It 'refuses when Production is paused' {
        { Assert-ModeAllowed @('app|paused') 'Development' } | Should Throw 'Production is running'
    }
    It 'allows each mode when only postgres runs' {
        { Assert-ModeAllowed @('postgres|running') 'Development' } | Should Not Throw
        { Assert-ModeAllowed @('postgres|running') 'Production' } | Should Not Throw
    }
    It 'allows a mode beside its own services and ignores stopped containers' {
        { Assert-ModeAllowed $dev 'Development' } | Should Not Throw
        { Assert-ModeAllowed $prod 'Production' } | Should Not Throw
        { Assert-ModeAllowed @('host|exited', 'web|exited', 'postgres|running') 'Production' } | Should Not Throw
        { Assert-ModeAllowed @('app|exited', 'cloudflared|exited') 'Development' } | Should Not Throw
    }
    It 'does not depend on container names (decided from the service label only)' {
        # a Development host created before the container_name rename is still the "host" service
        (Get-ActiveServices @('host|running') 'Development') -contains 'host' | Should Be $true
    }
    It 'treats an empty project as nothing running' {
        { Assert-ModeAllowed @() 'Production' } | Should Not Throw
    }
}
