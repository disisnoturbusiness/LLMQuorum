' Runs watchdog.ps1 with no visible window (window style 0), so the 10-minute check never flashes a console.
CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""C:\Temp\ForClaude\LLMQuorum\ops\watchdog.ps1""", 0, False
