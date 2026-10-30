using System.Text.Json;

namespace Weave.Actions.Skill;

public sealed record PostSkillInput(string WorkspaceId, JsonElement Skill);
