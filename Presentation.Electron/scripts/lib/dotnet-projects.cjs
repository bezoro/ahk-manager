// Shared by formatting and architecture checks so new projects cannot be omitted
// from one command while being accepted by another.
const dotnetProjects = [
  { directory: 'Core', project: 'Core/AhkManager.Core.csproj', dependencies: [] },
  {
    directory: 'Backend',
    project: 'Backend/AhkManager.Backend.csproj',
    dependencies: ['Core/AhkManager.Core.csproj'],
  },
  {
    directory: 'Tests.Backend',
    project: 'Tests.Backend/Tests.Backend.csproj',
    dependencies: ['Core/AhkManager.Core.csproj', 'Backend/AhkManager.Backend.csproj'],
    propertySources: ['$(BaselineCaptionProofSource)'],
  },
  {
    directory: 'UpdateHost',
    project: 'UpdateHost/AhkManager.UpdateHost.csproj',
    dependencies: [],
    linkedSources: ['Core/Infrastructure/WindowsStoragePath.cs'],
  },
  {
    directory: 'AgentCli',
    project: 'AgentCli/ahk-manager.csproj',
    dependencies: ['Core/AhkManager.Core.csproj'],
    propertySources: ['$(IntermediateOutputPath)AgentCatalog.g.cs'],
  },
  {
    directory: 'Tests.AgentCli',
    project: 'Tests.AgentCli/Tests.AgentCli.csproj',
    dependencies: [],
    linkedSources: [
      'AgentCli/AgentProtocol.cs',
      'AgentCli/AgentExchange.cs',
      'AgentCli/AgentDiagnostics.cs',
      'AgentCli/StableUpdateHandoff.cs',
    ],
  },
];

module.exports = { dotnetProjects };
