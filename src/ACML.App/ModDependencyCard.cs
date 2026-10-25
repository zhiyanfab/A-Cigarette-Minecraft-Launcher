using ACML.Core;
namespace ACML.App;
public sealed record ModDependencyCard(ModProject Project,string Kind)
{
    public string Title=>Project.Title;
    public string? IconUrl=>Project.IconUrl;
    public string KindLabel=>Kind=="required"?"必需前置":"可选依赖";
}
