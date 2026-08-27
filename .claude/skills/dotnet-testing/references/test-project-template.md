# .NET Test Project Template

Loaded on demand by `dotnet-testing` when a brand-new test project is required. When an existing test project exists, do not load this file; use what the project already references.

## Standard Project Structure

```
SolutionName/
  src/ProjectName/ProjectName.csproj
  tests/ProjectName.Tests/ProjectName.Tests.csproj
  SolutionName.sln
```

Adapt to the existing layout when one exists. For a brand-new test project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Shouldly" Version="4.*" />
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="coverlet.collector" Version="6.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ProjectName/ProjectName.csproj" />
  </ItemGroup>
</Project>
```

When an existing test project exists, do not add new packages -- use what is already referenced. Match the resolved Stack Profile (`test-framework`, `test-mocking`, `test-assertions`, `test-data`) when generating the `<PackageReference>` list above for a new project; the snippet shows the default xUnit + Shouldly + NSubstitute combination.
