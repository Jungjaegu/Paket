﻿namespace Paket

open System.IO

type InstallFiles = 
    { References : string Set
      FrameworkAssemblyReferences : string Set
      ContentFiles : string Set }
    
    static member empty = 
        { References = Set.empty
          FrameworkAssemblyReferences = Set.empty
          ContentFiles = Set.empty }
    
    member this.AddReference lib = { this with References = Set.add lib this.References }
    member this.AddFrameworkAssemblyReference assemblyName = { this with FrameworkAssemblyReferences = Set.add assemblyName this.FrameworkAssemblyReferences }

type InstallModel = 
    { PackageName : string
      PackageVersion : SemVerInfo
      Frameworks : Map<FrameworkIdentifier, InstallFiles> }

    static member EmptyModel(packageName, packageVersion) : InstallModel = 
        let frameworks = 
            [ for x, p in FrameworkVersion.KnownDotNetFrameworks -> DotNetFramework(Framework x, p) ]
        { PackageName = packageName
          PackageVersion = packageVersion
          Frameworks = List.fold (fun map f -> Map.add f InstallFiles.empty map) Map.empty frameworks }
    
    member this.GetFrameworks() = this.Frameworks |> Seq.map (fun kv -> kv.Key)
    member this.GetFiles(framework) = 
        match this.Frameworks.TryFind framework with
        | Some x -> x.References
        | None -> Set.empty

    member this.AddReference(framework,lib:string,references) : InstallModel = 
        let install =
            match references with
            | NuspecReferences.All -> true
            | NuspecReferences.Explicit list -> list |> List.exists lib.EndsWith

        if not install then this else
        { this with Frameworks = 
                     match Map.tryFind framework this.Frameworks with
                     | Some files -> Map.add framework (files.AddReference lib) this.Frameworks
                     | None -> Map.add framework (InstallFiles.empty.AddReference lib) this.Frameworks }

    member this.AddReferences(libs,references) : InstallModel =         
        libs |> Seq.fold (fun model lib -> 
                    match FrameworkIdentifier.DetectFromPath lib with
                    | Some framework -> model.AddReference(framework,lib,references)
                    | _ -> model) this

    member this.AddReferences(libs) = this.AddReferences(libs, NuspecReferences.All)

    member this.AddFrameworkAssemblyReference(framework,assemblyName) : InstallModel = 
        { this with Frameworks = 
                     match Map.tryFind framework this.Frameworks with
                     | Some files -> Map.add framework (files.AddFrameworkAssemblyReference assemblyName) this.Frameworks
                     | None -> Map.add framework (InstallFiles.empty.AddFrameworkAssemblyReference assemblyName) this.Frameworks }

    member this.AddFrameworkAssemblyReferences(references) : InstallModel  = 
        references
        |> Seq.fold (fun model reference -> model.AddFrameworkAssemblyReference(reference.TargetFramework,reference.AssemblyName)) this

    member this.FilterBlackList() =
        let blackList =
            [fun (f:string) -> not (f.EndsWith ".dll" || f.EndsWith ".exe")]

        { this with Frameworks = 
                     blackList 
                     |> List.fold 
                            (fun frameworks f ->  Map.map (fun _ files -> {files with References = files.References |> Set.filter (f >> not)}) frameworks) 
                        this.Frameworks }

    member this.UseGenericFrameworkVersionIfEmpty() =
        let genericFramework = DotNetFramework(All, Full)
        let newFiles = this.GetFiles genericFramework
               
        let model =
            if Set.isEmpty newFiles then this else

            let target = DotNetFramework(Framework FrameworkVersionNo.V1,Full)
            match Map.tryFind target this.Frameworks with
            | Some files when Set.isEmpty files.References |> not -> this
            | _ -> { this with Frameworks = Map.add target { References = newFiles; ContentFiles = Set.empty; FrameworkAssemblyReferences = Set.empty} this.Frameworks }

        { model with Frameworks = model.Frameworks |> Map.remove genericFramework } 

    member this.UseLowerVersionLibIfEmpty() = 
        FrameworkVersion.KnownDotNetFrameworks
        |> List.rev
        |> List.fold (fun (model : InstallModel) (lowerVersion,lowerProfile) -> 
               let newFiles = model.GetFiles(DotNetFramework(Framework lowerVersion, lowerProfile))
               if Set.isEmpty newFiles then model
               else 
                   FrameworkVersion.KnownDotNetFrameworks
                   |> List.filter (fun (version,profile) -> (version,profile) > (lowerVersion,lowerProfile))
                   |> List.fold (fun (model : InstallModel) (upperVersion,upperProfile) -> 
                          let framework = DotNetFramework(Framework upperVersion, upperProfile)
                          match Map.tryFind framework model.Frameworks with
                          | Some files when Set.isEmpty files.References -> 
                              { model with Frameworks = Map.add framework { References = newFiles; ContentFiles = Set.empty; FrameworkAssemblyReferences = Set.empty} model.Frameworks }
                          | _ -> model) model) this

    member this.UsePortableVersionLibIfEmpty() = 
        this.Frameworks 
        |> Seq.fold 
               (fun (model : InstallModel) kv -> 
               let newFiles = kv.Value.References
           
               if Set.isEmpty newFiles then model else

               let otherProfiles = 
                   match kv.Key with
                   | PortableFramework(_, f) -> 
                       f.Split([| '+' |], System.StringSplitOptions.RemoveEmptyEntries)
                       |> Array.map FrameworkIdentifier.Extract
                       |> Array.choose id
                   | _ -> [||]

               if Array.isEmpty otherProfiles then model else 
                otherProfiles 
                |> Array.fold (fun (model : InstallModel) framework -> 
                        match Map.tryFind framework model.Frameworks with
                        | Some files when Set.isEmpty files.References |> not -> model
                        | _ -> { model with Frameworks = Map.add framework { References = newFiles; FrameworkAssemblyReferences = Set.empty; ContentFiles = Set.empty} model.Frameworks }) model) this

    member this.Process() =
        this
            .UseGenericFrameworkVersionIfEmpty()            
            .UseLowerVersionLibIfEmpty()
            .UsePortableVersionLibIfEmpty()
            .UseLowerVersionLibIfEmpty()  // because we now might need to use portable
            .FilterBlackList()

    member this.GetLibraryNames =
        lazy([ for f in this.Frameworks do
                for lib in f.Value.References do                
                    let fi = new FileInfo(normalizePath lib)
                    yield fi.Name.Replace(fi.Extension,"") ]
            |> Set.ofList)

    static member CreateFromLibs(packageName,packageVersions,libs,nuspec:Nuspec) = 
        InstallModel.EmptyModel(packageName,packageVersions)
            .AddReferences(libs,nuspec.References)
            .AddFrameworkAssemblyReferences(nuspec.FrameworkAssemblyReferences)
            .Process()