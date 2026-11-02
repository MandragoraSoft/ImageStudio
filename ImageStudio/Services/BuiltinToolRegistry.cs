using System.IO;
using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Lista wbudowanych grup narzędzi czatu i ich fabryka. Nowa grupa = projekt ImageStudio.Tools.<Nazwa> z IChatToolGroup + wpis tutaj
// (jak ProviderRegistry dla dostawców)
public static class BuiltinToolRegistry
{
    public static BuiltinTools Create(IMemoryStore memory, string workspaceRoot, Func<ToolCall, bool> confirmWrite, TimeProvider? time = null, ISkillStore? skills = null)
    {
        List<IChatToolGroup> groups = [new DateTimeTools(time), new MemoryTools(memory, time), new FileTools(workspaceRoot)];
        if (skills is not null) groups.Add(new SkillTools(skills));
        return new BuiltinTools(groups, Path.GetFullPath(workspaceRoot), confirmWrite);
    }
}
