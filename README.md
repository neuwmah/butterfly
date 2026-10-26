# Butterfly

Software built to handle MixMaster Online disconnection issues.

## Stack

WPF desktop app on .NET 8 (`net8.0-windows`), with XAML views, a ViewModel for account/server/language state, and Win32/DWM P/Invoke to find, show, hide, and drive MixMaster windows. HttpClient talks to the license API and the self-update endpoint; Npgsql stores license records in PostgreSQL; HtmlAgilityPack scrapes ranking pages (HTML, XML, and JSON depending on the server); System.Management builds a hardware ID for license binding. Localization is swapped at runtime through XAML resource dictionaries (en-US, pt-BR, ko-KR, ja-JP, fr-FR). The WinExe ships with a custom icon, requests administrator elevation via the application manifest, and uses per-monitor DPI awareness.

## Structure

The repo is a single WPF project (`Butterfly.sln` / `Butterfly.csproj`). `App/` holds startup (`App.xaml`, `Program.cs`) and `app.manifest`. `Views/` covers Splash, License Key, Main, Console, and Add Account. `ViewModels/` and `Models/` back the account list, MixMaster servers (Source, Adventure, Origin), ranking cache, and stats. `Services/` handles license validation, auth, ranking fetch, hidden `.Butterfly` account persistence, localization, updates, and a small service locator. `Native/` wraps Win32 window and dark-title-bar APIs; `Converters/` and `Helpers/` support the UI, logging, and elevation checks. `Resources/Assets/` has the icon, `Resources/Flags/` the language flags, and `Resources/Languages/` the string dictionaries.
