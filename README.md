# gbOS86 Installer

A GUI installer for Googlebook OS that supports dual-boot.

## Requirements

- Windows 10/11 Pro
- Docker Desktop
- 16 GB RAM
- ~60 GB Storage for building the image itself

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## Usage

```powershell
dist\gbOS86-Installer.exe
```

## Note

This repo uses gbos-x86 to build a generic x86 image that works for 6th gen intel and higher. Go to their site (click on gbos-x86 submodule) for more details.
