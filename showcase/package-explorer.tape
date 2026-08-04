# VHS fork: https://github.com/alsi-lawr/vhs/tree/feat/xterm6-capture-modes
Output docs/showcase/package-explorer.webp

Require dotnet
Require dotnet-pe

Set Shell zsh
Set Theme "Tomorrow Night"
Set WindowBar ColorfulRight
Set WindowBarSize 16

Set FontFamily "MesloLGS Nerd Font"
Set FontSize 20

Set Width 1600
Set Height 900
Set BorderRadius 9
Set Padding 0
Set Margin 40
Set MarginFill "showcase/window-shadow.png"

Set Framerate 30
Set PlaybackSpeed 1
Set TypingSpeed 75ms
Set CursorBlink false

Type 'dotnet pe "$PACKAGE_EXPLORER_SHOWCASE_TARGET"'
Enter
Wait+Screen@60s /Browse/
Sleep 1s

Type "2"
Sleep 4s
Type "s"
Sleep 1s
Type "l"
Sleep 750ms
Type "j"
Sleep 750ms
Enter
Sleep 1s
Type "j"
Sleep 750ms
Type "k"
Sleep 750ms
Enter
Sleep 3s
Type "l"
Sleep 3s
Type "h"
Sleep 2s

Type "3"
Sleep 5s
Type "s"
Sleep 1s
Enter
Sleep 1s
Type "j"
Sleep 1s
Type " "
Sleep 1s
Type "p"
Sleep 4s
Type "l"
Sleep 1s
Type "l"
Sleep 1s
Type "h"
Sleep 1s

Type "1"
Sleep 3s
Type "/"
Type "newtonsoft"
Enter
Wait+Screen@10s /Searching packages/
Wait+Screen@30s /Ready/
Sleep 3s
