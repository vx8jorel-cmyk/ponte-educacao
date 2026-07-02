$key = (Get-Clipboard -Raw).Trim()
if ($key.Length -lt 20) {
    throw 'A área de transferência não contém uma chave válida.'
}

[Environment]::SetEnvironmentVariable('GEMINI_API_KEY', $key, 'User')
Remove-Variable key
Set-Clipboard -Value ''
Write-Output 'Chave armazenada no perfil local e removida da área de transferência.'
