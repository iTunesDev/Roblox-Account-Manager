# Roblox Account Manager

A Windows desktop app for managing several Roblox accounts and running them side by side.
It launches the normal, unmodified Roblox player. There are no cheats, injection or input automation.

## Features

**Accounts**
- Add accounts by signing in through the real roblox.com login page, by creating a new one on Roblox's own sign-up page, by pasting a cookie, or by importing a text file of cookies.
- Logins are encrypted with Windows DPAPI and stored only on this PC (`%LOCALAPPDATA%\RobloxAccountManager\accounts.dat`). Passwords are never stored.
- Nicknames, notes, groups, search and filtering. Avatar, Robux and Premium shown on each account.
- Account health: logins are re-checked at start-up and every 6 hours. Expired ones are flagged, and you can get a Discord alert for them.

**Launching**
- Multiple Roblox windows at once.
- **Launch separately**: each account joins on its own. Accounts with a region go to a public server in that region.
- **Join same server** (session joiner): every selected account ends up in one server. If one is already in a game, the others follow it.
- **Follow a friend**: send accounts to a friend's server, if the friend's privacy settings allow it.
- **Server browser**: list public servers with player counts, ping and region, then send accounts to one.
- **Server hop**: move running accounts to a different server of the same game.
- **Presets**: save accounts, game and join mode, launch them in one click, or from a desktop shortcut.
- Favorite and recently played games, with icons.

**AFK and performance**
- Low-resource mode for each account: minimized window, below-normal priority, limited CPU cores, regular memory trimming.
- A cap on how many Roblox windows can run at once.
- **Auto-rejoin**: relaunches an account if its window closes or it drops out of the game.
- Live CPU, RAM and uptime for each window.
- Tile all windows in a grid, or hide and show them all at once.
- Optional Discord webhook alerts for disconnects, rejoins and expired logins.
- Account name in each Roblox window's title bar.

**App**
- Windows 11 Fluent design (WPF-UI) with Mica, plus dark and light themes.
- Tray mode. Global hotkeys: Ctrl+Alt+H hides or shows the Roblox windows, Ctrl+Alt+T tiles them, Ctrl+Alt+M shows the app.
- Optional start with Windows, with a preset that launches automatically.

## Region ("geofence") notes
Roblox chooses servers near your real connection, and the app can't change where an account appears to connect from.
What it does is find public servers hosted in the region you choose and send the account there. Ping still depends on your real location.
To work out where a server is hosted, the app asks Roblox's matchmaker for the server's address (using one of your accounts) and looks that address up on ip-api.com. Only Roblox server addresses are sent.

## Download
Every push to `main` builds a ready-to-run `RobloxAccountManager.exe`. You'll find it on the **Actions** tab, under the latest run's *Artifacts*.
Tagging a commit `v1.0.0` (or similar) also attaches the exe to a GitHub Release.

You need Windows 10 or 11 and the Roblox player installed. Signing in inside the app uses the Microsoft Edge WebView2 Runtime, which is already on most Windows 10 and 11 PCs.

## Build it yourself
```
dotnet publish src/RobloxAccountManager -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Heads-up
- Roblox disconnects players after about 20 minutes idle, unless the game prevents it. This app doesn't fake input to get around that.
- A saved login works like a signed-in session. Keep your Windows account secure.
