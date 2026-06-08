@echo off
rem Watches wwwroot CSS sources and rebuilds tailwind.css on change (development use only).
tools\tailwindcss.exe -i src\QBModsBrowser.Server\wwwroot\tailwind-input.css -o src\QBModsBrowser.Server\wwwroot\tailwind.css --watch
