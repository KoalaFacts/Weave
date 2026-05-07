namespace Weave.Actions.Skill;

/// <summary>
/// Empty record for now — the silo's POST /skills endpoint returns 204 NoContent.
/// The presence of <see cref="ActionResult{T}.IsSuccess"/> is the entire signal.
/// </summary>
public sealed record PostSkillResult;
