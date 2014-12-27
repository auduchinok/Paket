﻿/// Contains methods for NuGet conversion
module Paket.NuGetConvert

open Paket
open System
open System.IO
open System.Xml
open Paket.Domain
open Paket.Logging
open Paket.Xml
open Paket.NuGetV2
open Paket.PackageSources
open Paket.Requirements

type ConvertMessage =
    | UnknownCredentialsMigrationMode of string
    | NugetPackagesConfigParseError of FileInfo
    | DependenciesFileAlreadyExists of FileInfo
    | ReferencesFileAlreadyExists of FileInfo

type CredsMigrationMode =
    | Encrypt
    | Plaintext
    | Selective

    static member Parse(s : string) = 
        match s with 
        | "encrypt" -> Rop.succeed Encrypt
        | "plaintext" -> Rop.succeed  Plaintext
        | "selective" -> Rop.succeed Selective
        | _ ->  UnknownCredentialsMigrationMode s |> Rop.failure

/// Represents type of NuGet packages.config file
type NugetPackagesConfigType = ProjectLevel | SolutionLevel

/// Represents NuGet packages.config file
type NugetPackagesConfig = {
    File: FileInfo
    Packages: (string*SemVerInfo) list
    Type: NugetPackagesConfigType
}
    
type ConvertResult = 
    { DependenciesFile : DependenciesFile
      ReferencesFiles : list<ReferencesFile>
      ProjectFiles : list<ProjectFile>
      SolutionFiles : list<SolutionFile>
      NugetConfigFiles : list<FileInfo>
      NugetPackagesFiles : list<NugetPackagesConfig>
      Force : bool
      CredsMigrationMode: CredsMigrationMode
      AutoVSPackageRestore : bool
      NugetTargets : option<FileInfo>
      NugetExe : option<FileInfo> }
    
    static member Empty(dependenciesFileName, force, credsMigrationMode) = 
        { DependenciesFile = 
            Paket.DependenciesFile(
                dependenciesFileName, 
                InstallOptions.Default, 
                [],
                [],
                [])
          ReferencesFiles = []
          ProjectFiles = []
          SolutionFiles = []
          NugetConfigFiles = []
          NugetPackagesFiles = [] 
          Force = force
          CredsMigrationMode = credsMigrationMode
          AutoVSPackageRestore = false
          NugetTargets = None
          NugetExe = None }

let private tryGetValue key (node : XmlNode) =
    node 
    |> getNodes "add"
    |> List.tryFind (getAttribute "key" >> (=) (Some key))
    |> Option.bind (getAttribute "value")

let private getKeyValueList (node : XmlNode) =
    node 
    |> getNodes "add"
    |> List.choose (fun node -> 
        match node |> getAttribute "key", node |> getAttribute "value" with
        | Some key, Some value -> Some(key, value)
        | _ -> None)

type NugetConfig = 
    { PackageSources : list<string * Auth option>
      PackageRestoreEnabled : bool
      PackageRestoreAutomatic : bool }

    static member empty =
        { PackageSources = [] 
          PackageRestoreEnabled = false 
          PackageRestoreAutomatic = false }

    member this.ApplyConfig (filename : string) =
        let doc = XmlDocument()
        doc.Load(filename)
        let config = 
            match doc |> getNode "configuration" with
            | Some node -> node
            | None -> failwithf "unable to parse %s" filename

        let clearSources = doc.SelectSingleNode("//packageSources/clear") <> null

        let getAuth key = 
            let getAuth' authNode =
                let userName = authNode |> tryGetValue "Username"
                let clearTextPass = authNode |> tryGetValue "ClearTextPassword"
                let encryptedPass = authNode |> tryGetValue "Password"

                match userName, encryptedPass, clearTextPass with 
                | Some userName, Some encryptedPass, _ -> 
                    Some { Username = userName; Password = ConfigFile.DecryptNuget encryptedPass }
                | Some userName, _, Some clearTextPass ->
                    Some  { Username = userName; Password = clearTextPass }
                | _ -> None

            config 
            |> getNode "packageSourceCredentials" 
            |> optGetNode (XmlConvert.EncodeLocalName key) 
            |> Option.bind getAuth'

        let sources = 
            config |> getNode "packageSources"
            |> Option.toList
            |> List.collect getKeyValueList
            |> List.map (fun (key,value) -> value, getAuth key)

        { PackageSources = if clearSources then sources else this.PackageSources @ sources
          PackageRestoreEnabled = 
            match config |> getNode "packageRestore" |> Option.bind (tryGetValue "enabled") with
            | Some value -> bool.Parse(value)
            | None -> this.PackageRestoreEnabled
          PackageRestoreAutomatic = 
            match config |> getNode "packageRestore" |> Option.bind (tryGetValue "automatic") with
            | Some value -> bool.Parse(value)
            | None -> this.PackageRestoreAutomatic }

let private readNugetConfig() =
    
    let config = 
        DirectoryInfo(".nuget")
        |> Seq.unfold (fun di -> if di = null 
                                 then None 
                                 else Some(FileInfo(Path.Combine(di.FullName, "nuget.config")), di.Parent)) 
        |> Seq.toList
        |> List.rev
        |> List.append [FileInfo(Path.Combine(Constants.AppDataFolder, "nuget", "nuget.config"))]
        |> List.filter (fun fi -> fi.Exists)
        |> List.fold (fun (config:NugetConfig) fi -> config.ApplyConfig fi.FullName) NugetConfig.empty
                     
    {config with PackageSources = if config.PackageSources = [] then [Constants.DefaultNugetStream, None] else config.PackageSources }

let readNugetPackagesConfig(file : FileInfo) = 
    try
        let doc = XmlDocument()
        doc.Load file.FullName
    
        { File = file
          Type = if file.Directory.Name = ".nuget" then SolutionLevel else ProjectLevel
          Packages = [for node in doc.SelectNodes("//package") ->
                            node.Attributes.["id"].Value, node.Attributes.["version"].Value |> SemVer.Parse ]}
        |> Rop.succeed 
    with _ -> Rop.failure (NugetPackagesConfigParseError file)

let readNugetPackages(convertResult) =
    FindAllFiles(Path.GetDirectoryName convertResult.DependenciesFile.FileName, "packages.config")
    |> Array.map readNugetPackagesConfig
    |> Rop.collect
    |> Rop.lift (fun xs -> {convertResult with NugetPackagesFiles = xs})

let collectNugetConfigs(convertResult) = 
    let nugetConfigs = 
        FindAllFiles(Path.GetDirectoryName convertResult.DependenciesFile.FileName, "nuget.config") 
        |> Array.toList

    Rop.succeed {convertResult with NugetConfigFiles = nugetConfigs}

let ensureNotAlreadyConverted(convertResult) =
    if convertResult.Force then Rop.succeed convertResult
    else 
        let depFile = 
            if File.Exists(convertResult.DependenciesFile.FileName) then 
                Rop.failure (DependenciesFileAlreadyExists(FileInfo(convertResult.DependenciesFile.FileName)))
            else Rop.succeed()

        let refFiles =
            convertResult.NugetPackagesFiles
            |> List.map (fun c -> Path.Combine(c.File.Directory.Name, Constants.ReferencesFile))
            |> List.map (fun r -> 
                   if File.Exists(r) then Rop.failure (ReferencesFileAlreadyExists <| FileInfo(r))
                   else Rop.succeed ())
            |> Rop.collect

        depFile 
        |> Rop.bind(fun _ -> 
            refFiles 
            |> Rop.bind (fun _ -> Rop.succeed convertResult))

let createDependenciesFile(convertResult) =
    
    let dependenciesFileName = convertResult.DependenciesFile.FileName
    let nugetConfig = readNugetConfig()

    let migrateCredentials sourceName auth =
        let credsMigrationMode = convertResult.CredsMigrationMode
        match credsMigrationMode with
        | Encrypt -> 
            ConfigAuthentication(auth.Username, auth.Password)
        | Plaintext -> 
            PlainTextAuthentication(auth.Username, auth.Password)
        | Selective -> 
            let question =
                sprintf "Credentials for source '%s': " sourceName  + 
                    "[encrypt and save in config (Yes) " + 
                    sprintf "| save as plaintext in %s (No)]" Constants.DependenciesFileName
                    
            match Utils.askYesNo question with
            | true -> ConfigAuthentication(auth.Username, auth.Password)
            | false -> PlainTextAuthentication(auth.Username, auth.Password)

    let sources = 
        nugetConfig.PackageSources 
        |> List.map (fun (name,auth) -> 
                        PackageSource.Parse(name, auth |> Option.map (migrateCredentials name)))
    
    let allVersions =
        convertResult.NugetPackagesFiles
        |> Seq.collect (fun c -> c.Packages)
        |> Seq.groupBy fst
        |> Seq.map (fun (name, packages) -> name, packages |> Seq.map snd |> Seq.distinct)
        |> Seq.sortBy (fun (name,_) -> name.ToLower())
    
    for (name, versions) in allVersions do
        if Seq.length versions > 1 
        then traceWarnfn "Package %s is referenced multiple times in different versions: %A. Paket will choose the latest one." 
                            name    
                            (versions |> Seq.map string |> Seq.toList)
    
    let latestVersions = allVersions |> Seq.map (fun (name,versions) -> name, versions |> Seq.max |> string) |> Seq.toList

    let existingDepFile = 
        if File.Exists dependenciesFileName
        then Some(DependenciesFile.ReadFromFile dependenciesFileName) 
        else None

    let conflictingPackages, packages = 
        match existingDepFile with
        | Some depFile -> latestVersions |> List.partition (fun (name,_) -> depFile.HasPackage (PackageName name))
        | None -> [], latestVersions
    
    for (name, _) in conflictingPackages do traceWarnfn "Package %s is already defined in %s" name dependenciesFileName

    let requirement (name : string, v : string) = 
        { Requirements.PackageRequirement.Name = PackageName name
          Requirements.PackageRequirement.VersionRequirement = 
              VersionRequirement(VersionRange.Specific(SemVer.Parse v), PreReleaseStatus.No)
          Requirements.PackageRequirement.ResolverStrategy = Max
          Requirements.PackageRequirement.Sources = sources
          Requirements.PackageRequirement.FrameworkRestrictions = []
          Requirements.PackageRequirement.Parent = 
              Requirements.PackageRequirementSource.DependenciesFile dependenciesFileName }

    let autoVsNugetRestore = nugetConfig.PackageRestoreEnabled && nugetConfig.PackageRestoreAutomatic
    let nugetTargets = FindAllFiles(Path.GetDirectoryName dependenciesFileName, "nuget.targets") |> Seq.firstOrDefault
    let nugetExe = FindAllFiles(Path.GetDirectoryName dependenciesFileName, "nuget.exe") |> Seq.firstOrDefault


    let dependenciesFile =
        match existingDepFile with
        | Some depFile ->
            packages 
            |> List.fold (fun (d : DependenciesFile) (name,version) -> d.Add(PackageName name,version)) depFile
        | None -> 
            Paket.DependenciesFile(
                dependenciesFileName, 
                InstallOptions.Default, 
                sources, 
                packages |> List.map requirement, 
                []) 
    
        |> (fun d -> if nugetExe.IsSome then d.Add(PackageName "Nuget.CommandLine","") else d)
    

    Rop.succeed 
        {convertResult with 
            DependenciesFile = dependenciesFile
            AutoVSPackageRestore = autoVsNugetRestore
            NugetTargets = nugetTargets
            NugetExe = nugetExe}

let createReferencesFiles(convertResult) =
    let createSingle packagesConfig = 
        let fileName = Path.Combine(packagesConfig.File.Directory.Name, Constants.ReferencesFile)
        packagesConfig.Packages
        |> List.map (fst >> PackageName)
        |> List.fold (fun (r : ReferencesFile) packageName -> r.AddNuGetReference(packageName)) 
                     (ReferencesFile.New(fileName))

    let referencesFiles = 
        convertResult.NugetPackagesFiles 
        |> List.filter (fun c -> c.Type <> SolutionLevel)
        |> List.map createSingle

    Rop.succeed {convertResult with ReferencesFiles = referencesFiles}
   
let convertSolutions(convertResult) = 
    let dependenciesFileName = convertResult.DependenciesFile.FileName
    let root = Path.GetDirectoryName dependenciesFileName
    let solutions =
        FindAllFiles(root, "*.sln")
        |> Array.map(fun fi -> SolutionFile(fi.FullName))
        |> Array.toList

    for solution in solutions do
        let dependenciesFileRef = createRelativePath solution.FileName dependenciesFileName
        solution.RemoveNugetEntries()
        solution.AddPaketFolder(dependenciesFileRef, None)

    Rop.succeed {convertResult with SolutionFiles = solutions}

let convertProjects(convertResult) = 
    let dependenciesFileName = convertResult.DependenciesFile.FileName
    let root = Path.GetDirectoryName dependenciesFileName
    let projects = ProjectFile.FindAllProjects root |> Array.toList
    for project in projects do 
        project.ReplaceNuGetPackagesFile()
        project.RemoveNuGetTargetsEntries()

    Rop.succeed {convertResult with ProjectFiles = projects}
    
let convert(dependenciesFileName, force, credsMigrationMode) =
    let credsMigrationMode =
        defaultArg 
            (credsMigrationMode |> Option.map CredsMigrationMode.Parse)
            (Rop.succeed Encrypt)
    
    credsMigrationMode 
    |> Rop.bind (fun mode -> ConvertResult.Empty(dependenciesFileName, force, mode) |> Rop.succeed)
    |> Rop.bind readNugetPackages
    |> Rop.bind collectNugetConfigs
    |> Rop.bind ensureNotAlreadyConverted
    |> Rop.bind createDependenciesFile
    |> Rop.bind createReferencesFiles
    |> Rop.bind convertSolutions
    |> Rop.bind convertProjects
