&larr; [README](README.md)

This file provides more technical documentation about SMAPI. If you only want to use or create
mods, this section isn't relevant to you; see the main README to use or create mods.

# Contents
* [SMAPI](#smapi)
  * [Development](#development)
    * [Compiling from source](#compiling-from-source)
    * [Debugging a local build](#debugging-a-local-build)
    * [Preparing a release](#preparing-a-release)
  * [Customisation](#customisation)
    * [Configuration file](#configuration-file)
    * [Command-line arguments](#command-line-arguments)
    * [Compile flags](#compile-flags)
* [SMAPI web services](#smapi-web-services)
  * [Overview](#overview)
    * [Log parser](#log-parser)
    * [Mods API](#mods-api)
  * [Development](#development-2)
    * [Local development](#local-development)
    * [Deploying to Amazon Beanstalk](#deploying-to-amazon-beanstalk)

# SMAPI
## Development
### Compiling from source
Using an official SMAPI release is recommended for most users.

SMAPI uses some C# 7 code, so you'll need at least
[Visual Studio 2017](https://www.visualstudio.com/vs/community/) on Windows,
[MonoDevelop 7.0](http://www.monodevelop.com/) on Linux,
[Visual Studio 2017 for Mac](https://www.visualstudio.com/vs/visual-studio-mac/), or an equivalent
IDE to compile it. It uses build configuration derived from the
[crossplatform mod config](https://github.com/Pathoschild/Stardew.ModBuildConfig#readme) to detect
your current OS automatically and load the correct references. Compile output will be placed in a
`bin` folder at the root of the git repository.

### Debugging a local build
Rebuilding the solution in debug mode will copy the SMAPI files into your game folder. Starting
the `StardewModdingAPI` project with debugging from Visual Studio (on Mac or Windows) will launch
SMAPI with the debugger attached, so you can intercept errors and step through the code being
executed. This doesn't work in MonoDevelop on Linux, unfortunately.

### Preparing a release
To prepare a crossplatform SMAPI release, you'll need to compile it on two platforms. See
[crossplatforming info](http://stardewvalleywiki.com/Modding:Creating_a_SMAPI_mod#Test_on_all_platforms)
on the wiki for the first-time setup.

1. Update the version number in `GlobalAssemblyInfo.cs` and `Constants::Version`. Make sure you use a
   [semantic version](http://semver.org). Recommended format:

   build type | format                            | example
   :--------- | :-------------------------------- | :------
   dev build  | `<version>-alpha.<timestamp>`     | `2.0-alpha.20171230`
   prerelease | `<version>-prerelease.<ID>`       | `2.0-prerelease.2`
   release    | `<version>`                       | `2.0`

2. In Windows:
   1. Rebuild the solution in _Release_ mode.
   2. Rename `bin/Packaged` to `SMAPI <version>` (e.g. `SMAPI 2.0`).
   2. Transfer the `SMAPI <version>` folder to Linux or Mac.  
      _This adds the installer executable and Windows files. We'll do the rest in Linux or Mac,
      since we need to set Unix file permissions that Windows won't save._

2. In Linux or Mac:
   1. Rebuild the solution in _Release_ mode.
   2. Copy `bin/internal/Packaged/Mono` into the `SMAPI <version>` folder.
   3. If you did everything right so far, you should have a folder like this:

      ```
      SMAPI-2.x/
         install.exe
         readme.txt
         internal/
            Mono/
               Mods/*
               Mono.Cecil.dll
               Newtonsoft.Json.dll
               StardewModdingAPI
               StardewModdingAPI.AssemblyRewriters.dll
               StardewModdingAPI.config.json
               StardewModdingAPI.exe
               StardewModdingAPI.pdb
               StardewModdingAPI.xml
               steam_appid.txt
               System.Numerics.dll
               System.Runtime.Caching.dll
               System.ValueTuple.dll
            Windows/
               Mods/*
               Mono.Cecil.dll
               Newtonsoft.Json.dll
               StardewModdingAPI.AssemblyRewriters.dll
               StardewModdingAPI.config.json
               StardewModdingAPI.exe
               StardewModdingAPI.pdb
               StardewModdingAPI.xml
               System.ValueTuple.dll
               steam_appid.txt
      ```
   4. Open a terminal in the `SMAPI <version>` folder and run `chmod 755 internal/Mono/StardewModdingAPI`.
   5. Copy & paste the `SMAPI <version>` folder as `SMAPI <version> for developers`.
   6. In the `SMAPI <version>` folder...
      * edit `internal/Mono/StardewModdingAPI.config.json` and
        `internal/Windows/StardewModdingAPI.config.json` to disable developer mode;
      * delete `internal/Windows/StardewModdingAPI.xml`.
   7. Compress the two folders into `SMAPI <version>.zip` and `SMAPI <version> for developers.zip`.

## Customisation
### Configuration file
You can customise the SMAPI behaviour by editing the `StardewModdingAPI.config.json` file in your
game folder.

Basic fields:

field             | purpose
----------------- | -------
`DeveloperMode`   | Default `false` (except in _SMAPI for developers_ releases). Whether to enable features intended for mod developers (mainly more detailed console logging).
`CheckForUpdates` | Default `true`. Whether SMAPI should check for a newer version when you load the game. If a new version is available, a small message will appear in the console. This doesn't affect the load time even if your connection is offline or slow, because it happens in the background.
`VerboseLogging`  | Default `false`. Whether SMAPI should log more information about the game context.
`ModData`         | Internal metadata about SMAPI mods. Changing this isn't recommended and may destabilise your game. See documentation in the file.

### Command-line arguments
The SMAPI installer recognises three command-line arguments:

argument | purpose
-------- | -------
`--install` | Preselects the install action, skipping the prompt asking what the user wants to do.
`--uninstall` | Preselects the uninstall action, skipping the prompt asking what the user wants to do.
`--game-path "path"` | Specifies the full path to the folder containing the Stardew Valley executable, skipping automatic detection and any prompt to choose a path. If the path is not valid, the installer displays an error.

SMAPI itself recognises two arguments, but these are intended for internal use or testing and may
change without warning.

argument | purpose
-------- | -------
`--log-path "path"` | The relative or absolute path of the log file SMAPI should write.
`--no-terminal` | SMAPI won't write anything to the console window. (Messages will still be written to the log file.)

### Compile flags
SMAPI uses a small number of conditional compilation constants, which you can set by editing the
`<DefineConstants>` element in `StardewModdingAPI.csproj`. Supported constants:

flag | purpose
---- | -------
`SMAPI_FOR_WINDOWS` | Indicates that SMAPI is being compiled on Windows for players on Windows. Set automatically in `crossplatform.targets`.


# SMAPI web services
## Overview
The `StardewModdingAPI.Web` project provides an API and web UI hosted at `*.smapi.io`.

### Log parser
The log parser provides a web UI for uploading, parsing, and sharing SMAPI logs. The logs are
persisted in a compressed form to Pastebin.

The log parser lives at https://log.smapi.io.

### Mods API
The mods API provides version info for mods hosted by Chucklefish, GitHub, or Nexus Mods. It's used
by SMAPI to perform update checks. The `{version}` URL token is the version of SMAPI making the
request; it doesn't do anything currently, but lets us version breaking changes if needed.

Each mod is identified by a repository key and unique identifier (like `nexus:541`). The following
repositories are supported:

key           | repository
------------- | ----------
`chucklefish` | A mod page on the [Chucklefish mod site](https://community.playstarbound.com/resources/categories/22), identified by the mod ID in the page URL.
`github`      | A repository on [GitHub](https://github.com), identified by its owner and repository name (like `Zoryn4163/SMAPI-Mods`). This checks the version of the latest repository release.
`nexus`       | A mod page on [Nexus Mods](https://www.nexusmods.com/stardewvalley), identified by the mod ID in the page URL.


The API accepts either `GET` or `POST` for convenience:
> ```
>GET https://api.smapi.io/v2.0/mods?modKeys=nexus:541,chucklefish:4228
>```

>```
>POST https://api.smapi.io/v2.0/mods
>{
>   "ModKeys": [ "nexus:541", "chucklefish:4228" ]
>}
>```

It returns a response like this:
>```
>{
>  "chucklefish:4228": {
>    "name": "Entoarox Framework",
>    "version": "1.8.0",
>    "url": "https://community.playstarbound.com/resources/4228"
>  },
>  "nexus:541": {
>    "name": "Lookup Anything",
>    "version": "1.16",
>    "url": "http://www.nexusmods.com/stardewvalley/mods/541"
>  }
>}
>```

## Development
### Local development
`StardewModdingAPI.Web` is a regular ASP.NET MVC Core app, so you can just launch it from within
Visual Studio to run a local version.

There are two differences when it's run locally: all endpoints use HTTP instead of HTTPS, and the
subdomain portion becomes a route (e.g. `log.smapi.io` &rarr; `localhost:59482/log`).

Before running it locally, you need to enter your credentials in the `appsettings.Development.json`
file. See the next section for a description of each setting. This file is listed in `.gitignore`
to prevent accidentally committing credentials.

### Deploying to Amazon Beanstalk
The app can be deployed to a standard Amazon Beanstalk IIS environment. When creating the
environment, make sure to specify the following environment properties:

property name                   | description
------------------------------- | -----------------
`LogParser:PastebinDevKey`      | The [Pastebin developer key](https://pastebin.com/api#1) used to authenticate with the Pastebin API.
`LogParser:PastebinUserKey`     | The [Pastebin user key](https://pastebin.com/api#8) used to authenticate with the Pastebin API. Can be left blank to post anonymously.
`LogParser:SectionUrl`          | The root URL of the log page, like `https://log.smapi.io/`.
`ModUpdateCheck:GitHubPassword` | The password with which to authenticate to GitHub when fetching release info.
`ModUpdateCheck:GitHubUsername` | The username with which to authenticate to GitHub when fetching release info.
