using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace AutoChessTactics;

/// <summary>特殊卡的合成策略。</summary>
public enum SynthesisCardPolicy
{
    /// <summary>所有 DynamicVars 都来自常规卡牌数值，可以直接缩放。</summary>
    Automatic,

    /// <summary>充能球由 OrbSynthesisService 单独处理数量。</summary>
    Orb,

    /// <summary>保留选择流程，只缩放明确安全的伤害/格挡等变量。</summary>
    ComplexChoice,

    /// <summary>保留召唤次数，只缩放召唤单位的基础属性。</summary>
    Summoner,

    /// <summary>结构性动作只执行一次，只缩放少量明确数值。</summary>
    DeckStructure,

    /// <summary>状态/事件监听只注册一次，只缩放明确安全数值。</summary>
    StatefulEvent,

    /// <summary>没有可靠数值映射时只保留星级，不改变触发次数。</summary>
    Conservative,
}

/// <summary>单张卡的合成规则。</summary>
public sealed class SynthesisCardRule
{
    public SynthesisCardRule(SynthesisCardPolicy policy, params string[] scalableVars)
    {
        Policy = policy;
        ScalableVars = new HashSet<string>(scalableVars, StringComparer.OrdinalIgnoreCase);
    }

    public SynthesisCardPolicy Policy { get; }

    /// <summary>
    /// SPECIAL 卡只允许缩放这里列出的变量。
    /// 例如召唤卡不把 Amount/Repeat 当成召唤数量放大。
    /// </summary>
    public IReadOnlySet<string> ScalableVars { get; }

    public bool CanScale(string variableName)
    {
        return Policy == SynthesisCardPolicy.Automatic
            || ScalableVars.Contains(variableName);
    }
}

/// <summary>
/// 卡牌合成数据库。
///
/// AutoMergeCards 是可直接缩放的白名单；SpecialCards 现在也允许合成，
/// 但由逐卡策略决定哪些数值可以安全放大。没有注册策略的 SPECIAL 卡使用
/// Conservative：可以升星，但原效果、选择次数和结构动作保持不变。
/// </summary>
public static partial class SynthesisDatabase
{
    private static readonly Lazy<HashSet<string>> _auto = new(() =>
        new HashSet<string>(AutoMergeCards ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));

    private static readonly Lazy<HashSet<string>> _special = new(() =>
        new HashSet<string>(SpecialCards ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));

    private static readonly Lazy<Dictionary<string, SynthesisCardRule>> _rules = new(BuildRules);

    private static readonly SynthesisCardRule _automatic =
        new(SynthesisCardPolicy.Automatic);

    private static readonly SynthesisCardRule _conservative =
        new(SynthesisCardPolicy.Conservative);

    public static bool IsAutoMergeableEntry(string entry)
    {
        return entry != null && _auto.Value.Contains(entry);
    }

    public static bool IsSpecialEntry(string entry)
    {
        return entry != null && _special.Value.Contains(entry);
    }

    /// <summary>返回卡牌的逐卡策略，未知 SPECIAL 使用保守策略。</summary>
    public static SynthesisCardRule GetRule(CardModel card)
    {
        if (card == null)
        {
            return _conservative;
        }
        return GetRule(card.Id.Entry);
    }

    public static SynthesisCardRule GetRule(string entry)
    {
        if (entry != null && OrbSynthesisService.IsOrbCard(entry))
        {
            return new SynthesisCardRule(SynthesisCardPolicy.Orb);
        }
        if (entry != null && _rules.Value.TryGetValue(entry, out SynthesisCardRule? rule))
        {
            return rule;
        }
        if (entry != null && IsAutoMergeableEntry(entry))
        {
            return _automatic;
        }
        return _conservative;
    }

    /// <summary>判断一张卡牌能否进入合成选择界面。</summary>
    public static bool IsMergeable(CardModel card)
    {
        if (card == null)
        {
            return false;
        }
        if (card.Keywords.Contains(CardKeyword.Unplayable) || card.Type == CardType.Curse)
        {
            return false;
        }

        // 附魔卡不再在这里拒绝；两张卡的附魔兼容性在合成事务开始前校验。
        return IsAutoMergeableEntry(card.Id.Entry)
            || IsSpecialEntry(card.Id.Entry)
            || OrbSynthesisService.IsOrbCard(card.Id.Entry);
    }

    /// <summary>
    /// 判断某个 DynamicVar 是否允许按星级缩放。
    /// 该入口集中处理特殊卡，避免 SynthesisService 不小心把结构变量放大。
    /// </summary>
    public static bool ShouldScaleDynamicVar(CardModel card, string variableName)
    {
        if (card == null || string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        SynthesisCardRule rule = GetRule(card);
        if (rule.Policy == SynthesisCardPolicy.Orb)
        {
            // 球类卡的 Repeat 是“球数量”，由 OrbSynthesisService 按星级处理；
            // 其它基础数值（例如闪电伤害、冰川格挡）仍然可以正常缩放。
            return !variableName.Equals("Repeat", StringComparison.OrdinalIgnoreCase);
        }
        return rule.CanScale(variableName);
    }

    /// <summary>建立 SPECIAL 卡规则注册表。</summary>
    private static Dictionary<string, SynthesisCardRule> BuildRules()
    {
        var result = new Dictionary<string, SynthesisCardRule>(StringComparer.OrdinalIgnoreCase);

        // 这些变量是卡牌基础伤害/格挡/持续时间，不能代表“再选择一次”。
        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "Damage", "Block" },
            "alchemize", "calculated_gamble", "discovery", "distraction", "double_energy",
            "enlightenment", "havoc", "infernal_blade", "juggling", "master_planner",
            "mayhem", "nightmare", "rainbow", "royal_gamble", "scrawl", "secret_technique",
            "secret_weapon", "seeking_edge", "squeeze", "stack", "stratagem", "tools_of_the_trade",
            "tutor", "white_noise", "well_laid_plans", "wish");

        // 召唤次数不缩放；只处理明确命名为基础战斗属性的变量。
        Add(result, SynthesisCardPolicy.Summoner,
            new[] { "Damage", "Block", "Health", "MaxHealth", "Attack", "Duration" },
            "beckon", "bodyguard", "byrdonis_egg", "eidolon", "guards", "legion_of_bone",
            "reanimate", "summon_forth", "underworld");

        // 牌组结构动作（生成、移除、复制、抽取）保持一次。
        Add(result, SynthesisCardPolicy.DeckStructure,
            new[] { "Damage", "Block", "Duration" },
            "abundance", "aggression", "cleanse", "conqueror", "forbidden_grimoire",
            "fusion", "hellraiser", "hibernate", "infinite_blades", "lantern_key",
            "mad_science", "necro_mastery", "nostalgia", "prolong", "sacrifice",
            "spoils_map", "subroutine", "trash_to_treasure", "tyranny", "unleash");

        // 事件和状态效果不会重复注册监听器，也不放大一次性触发次数。
        Add(result, SynthesisCardPolicy.StatefulEvent,
            new[] { "Damage", "Block", "Duration", "Power" },
            "afterlife", "bad_luck", "barricade", "beacon_of_hope", "bullet_time",
            "calamity", "dark_embrace", "death_march", "debt", "enthralled", "entrench",
            "furnace", "greed", "largesse", "malaise", "murder", "poor_sleep",
            "reaper_form", "regret", "reaper_form", "royalties", "soulbound", "times_up",
            "unmovable");

        // Osty 体系使用了几组独立变量名：
        //   - OstyDamage：由 Osty 发起的攻击伤害；
        //   - Summon：OstyCmd.Summon 的生命/召唤强度，不是“召唤次数”；
        //   - Heal：恢复量；
        //   - CalculationBase/ExtraDamage：Protector/Squeeze/Unleash 这类公式型 Osty 攻击的基础项。
        // 这些卡原本落在 SPECIAL 的保守路径里，只显示星级但不改数值；这里逐卡覆盖，
        // 同时仍然不放大选择次数、X 次数、抽牌数量或结构性操作次数。
        Add(result, SynthesisCardPolicy.Summoner,
            new[] { "Summon", "Heal" },
            "afterlife", "bodyguard", "cleanse", "dirge", "legion_of_bone",
            "necro_mastery", "reanimate", "spur");

        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "OstyDamage" },
            "flatten", "poke", "snap", "sweeping_gaze");

        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "CalculationBase", "ExtraDamage" },
            "protector", "squeeze", "unleash");

        // 储君/Regent 里有几张 SPECIAL 卡的可缩放值不是 Damage/Block：
        // 铸剑者(the_smith) 使用 Forge，王国资产(royalties) 使用 Gold。
        // 单独注册能让它们升星后数值增长，同时不放大额外奖励次数或事件次数。
        Add(result, SynthesisCardPolicy.StatefulEvent,
            new[] { "Forge" },
            "the_smith");

        Add(result, SynthesisCardPolicy.StatefulEvent,
            new[] { "Gold" },
            "royalties");

        // 天际钻头的 Energy=4 是“花费达到 4 时连击翻倍”的阈值，不是收益数值。
        // 它在 AUTO 列表里，所以这里用显式规则覆盖自动缩放，只放大 Damage，保留阈值 4。
        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "Damage" },
            "heavenly_drill");

        // 这两张卡有永久成长字段：CurrentDamage/CurrentBlock 与 Increased*。
        // 规则层只声明 Damage/Block 和每次成长量 Increase 可缩放；
        // SynthesisService 会额外保留成长字段，避免升星/克隆/读档把成长重置。
        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "Damage", "Increase" },
            "the_scythe");

        Add(result, SynthesisCardPolicy.ComplexChoice,
            new[] { "Block", "Increase" },
            "genetic_algorithm");

        return result;
    }

    private static void Add(
        Dictionary<string, SynthesisCardRule> rules,
        SynthesisCardPolicy policy,
        IReadOnlyList<string> scalableVars,
        params string[] entries)
    {
        foreach (string entry in entries)
        {
            rules[entry] = new SynthesisCardRule(policy, scalableVars.ToArray());
        }
    }
}
