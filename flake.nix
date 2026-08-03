{
  description = ".NET 10 development shell for dotnet-package-explorer";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { nixpkgs, ... }:
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
    in
    {
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

      checks = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          development-tools = pkgs.runCommand "dotnet-package-explorer-development-tools" {
            nativeBuildInputs = developmentTools pkgs;
          } ''
            export DOTNET_CLI_HOME="$TMPDIR"
            dotnet --version | grep -E '^10[.]'
            git --version
            fantomas --version | grep -F 'Fantomas v7.0.5'
            fsautocomplete --version
            touch "$out"
          '';
        }
      );
    };
}
