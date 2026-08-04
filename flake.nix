{
  description = "Visual Studio-style NuGet package explorer for .NET terminals";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  inputs.workspace-explorer = {
    url = "github:alsi-lawr/dotnet-workspace-explorer/v0.3.0";
    inputs.nixpkgs.follows = "nixpkgs";
  };

  outputs =
    { nixpkgs, workspace-explorer, ... }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
      developmentTools = pkgs:
        let
          fantomas_7_0_5 = pkgs.buildDotnetGlobalTool {
            pname = "fantomas";
            version = "7.0.5";
            nugetHash = "sha256-fseS0ORahl/iK/uZmGOooTmrny8YL1KEwNNq27VxLj0=";
            dotnet-runtime = pkgs.dotnet-sdk_10;
          };
        in
        [
          pkgs.dotnet-sdk_10
          pkgs.git
          fantomas_7_0_5
          pkgs.fsautocomplete
        ];
      packageExplorer = system:
        let
          pkgs = import nixpkgs { inherit system; };
          inherit (pkgs) lib;
        in
        pkgs.buildDotnetModule {
          pname = "dotnet-package-explorer";
          version = "0.1.0";

          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [
              ./Directory.Build.props
              ./Directory.Packages.props
              ./global.json
              ./src/Application
              ./src/RpcClient
              ./src/Terminal
            ];
          };

          projectFile = "src/Terminal/Dotnet.PackageExplorer.Terminal.fsproj";
          nugetDeps = ./nix/deps.json;
          dotnet-sdk = pkgs.dotnet-sdk_10;
          dotnet-runtime = pkgs.dotnet-runtime_10;
          selfContainedBuild = true;
          executables = [ "dotnet-pe" ];
          makeWrapperArgs = [
            "--prefix"
            "PATH"
            ":"
            "${workspace-explorer.packages.${system}.default}/bin"
          ];

          meta = {
            description = "Visual Studio-style NuGet package explorer for .NET terminals";
            homepage = "https://github.com/alsi-lawr/dotnet-package-explorer";
            license = lib.licenses.mit;
            mainProgram = "dotnet-pe";
          };
        };
    in
    {
      packages = forAllSystems (system:
        let
          package = packageExplorer system;
        in
        {
          default = package;
          dotnet-package-explorer = package;
        });

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${packageExplorer system}/bin/dotnet-pe";
        };
      });

      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          default = pkgs.mkShellNoCC {
            packages = developmentTools pkgs;

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            NUGET_XMLDOC_MODE = "skip";
          };
        }
      );

    };
}
