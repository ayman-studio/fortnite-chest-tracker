using Unreal.Core.Attributes;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports.Weapons;

/// <summary>
/// Patch: generic weapon/gizmo actor. Treasure hunt items (golden firework, relic extractor...)
/// are aliased onto this group through <see cref="Unreal.Core.NetFieldParser.AliasResolver"/>.
/// </summary>
[NetFieldExportGroup(GenericWeapon.GroupPath, minimalParseMode: ParseMode.Minimal)]
public class GenericWeapon : BaseWeapon
{
    public const string GroupPath = "__Alias.GenericWeapon";
}
