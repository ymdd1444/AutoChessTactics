using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoChessTactics;

/// <summary>
/// 内部自测（用于 TestHarness / 开发验证；设置 AUTOCHESS_SELFTEST=1 时也可在游戏启动时运行）。
///
/// 为什么放在 Mod 里而不是测试工程里：
/// .NET 9 的 apphost 在启动时会解析“入口程序集”的【直接】程序集引用。
/// 如果测试程序直接引用 sts2，会在我们注册 AssemblyResolve 处理器（模块初始化器）之前
/// 就崩溃。而 Mod 本身引用 sts2 是正常的（游戏里由宿主解析），
/// 所以把逻辑断言放进 Mod，测试程序只通过反射调用本方法。
/// </summary>
internal static class SelfTest
{
    /// <summary>运行全部自测，返回可读的结果文本（不依赖 Godot Log，测试环境可用）。</summary>
    public static string RunAll()
    {
        var sb = new StringBuilder();
        int failures = 0;

        Run(sb, ref failures, "Harmony 补丁已挂载到关键克隆入口", TestHarmonyPatchCoverage);
        Run(sb, ref failures, "同组语义（同 id+同星，升级可不同）", TestSameGroup);
        Run(sb, ref failures, "保留升级较高者（任一带+结果带+）", TestChooseBase);
        Run(sb, ref failures, "合成数值（打击 6->9->18；打击+ 9->13->27）", TestScaling);
        Run(sb, ref failures, "星级缩放不串卡（只改目标卡）", TestScalingIsolation);
        Run(sb, ref failures, "合成卡是独立实例（原卡数值不被改写）", TestIndependentSynthesisCard);
        Run(sb, ref failures, "DeckVersion 克隆有效星级（事件/战斗克隆不降星）", TestDeckVersionEffectiveStar);
        Run(sb, ref failures, "克隆星级复制逻辑链式不降星", TestCreateCloneChainKeepsStar);
        Run(sb, ref failures, "保存补丁（星级写入存档）", TestSavePatch);
        Run(sb, ref failures, "SL 兜底恢复（数值还在时救回星级）", TestSavePatchRecoversScaledCardAfterSl);
        Run(sb, ref failures, "标题兜底恢复（第一次 SL 后仍显示星级）", TestTitlePatchRecoversScaledCardAfterSl);
        Run(sb, ref failures, "兜底恢复不误判升级一星卡", TestRecoveryDoesNotMistakeUpgradedStrike);
        Run(sb, ref failures, "保存补丁去重（避免旧星级覆盖新星级）", TestSavePatchDeduplicatesStar);
        Run(sb, ref failures, "读档补丁（星级+数值恢复）", TestLoadPatch);
        Run(sb, ref failures, "读档补丁兼容重复星级键（取最高星）", TestLoadPatchDuplicateStars);
        Run(sb, ref failures, "标题星级后缀（★★★）", TestTitlePatch);
        Run(sb, ref failures, "SPECIAL 卡规则（复杂/召唤/结构/状态）", TestSpecialRules);
        Run(sb, ref failures, "附魔兼容性（同类型同数量）", TestEnchantmentCompatibility);

        sb.AppendLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
        return sb.ToString();
    }

    private static void Run(StringBuilder sb, ref int failures, string name, Action test)
    {
        try
        {
            test();
            sb.AppendLine("  ✓ " + name);
        }
        catch (Exception e)
        {
            failures++;
            sb.AppendLine("  ✗ " + name + " => " + e);
        }
    }

    private static CardModel NewCard<T>() where T : CardModel
    {
        EnsureModelAvailable(typeof(T));
        return ModelDb.GetById<CardModel>(ModelDb.GetId(typeof(T))).ToMutable();
    }

    private static void EnsureModelAvailable(Type type)
    {
        if (!ModelDb.Contains(type))
        {
            ModelDb.Inject(type);
        }
    }

    private static void TestHarmonyPatchCoverage()
    {
        AutoChessTactics.EnsureHarmonyPatchesApplied();

        Assert(
            HasAutoChessPostfix(typeof(CardModel).GetMethod(nameof(CardModel.CreateClone), Type.EmptyTypes)),
            "CardModel.CreateClone 缺少 AutoChess 星级复制补丁");
        AssertPatchedIfExists(
            "CreateCloneForPlayer",
            new[] { typeof(Player) },
            "CardModel.CreateCloneForPlayer 存在但缺少 AutoChess 星级复制补丁");
        AssertAllCardMethodsPatched(
            "CreateDupe",
            method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return method.ReturnType == typeof(CardModel)
                    && (parameters.Length == 0
                        || (parameters.Length == 1 && parameters[0].ParameterType == typeof(Player)));
            },
            "CardModel.CreateDupe 缺少 AutoChess 星级复制补丁");
        Assert(
            HasAutoChessPostfix(typeof(RunState).GetMethod(nameof(RunState.CloneCard), new[] { typeof(CardModel) })),
            "RunState.CloneCard 缺少 AutoChess 星级复制补丁");
        Assert(
            HasAutoChessPostfix(typeof(CombatState).GetMethod(nameof(CombatState.CloneCard), new[] { typeof(CardModel) })),
            "CombatState.CloneCard 缺少 AutoChess 星级复制补丁");
    }

    private static bool HasAutoChessPostfix(MethodBase? method)
    {
        Assert(method != null, "找不到需要检查的游戏方法");
        var patchInfo = Harmony.GetPatchInfo(method!);
        return patchInfo?.Postfixes.Any(patch => patch.owner == AutoChessTactics.HarmonyId) == true;
    }

    private static void AssertPatchedIfExists(string name, Type[] parameters, string message)
    {
        MethodInfo? method = typeof(CardModel).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameters,
            modifiers: null);
        if (method == null)
        {
            return;
        }

        Assert(HasAutoChessPostfix(method), message);
    }

    private static void AssertAllCardMethodsPatched(
        string name,
        Func<MethodInfo, bool> predicate,
        string message)
    {
        MethodInfo[] methods = typeof(CardModel)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == name && predicate(method))
            .ToArray();
        Assert(methods.Length > 0, "找不到需要检查的游戏方法：" + name);
        foreach (MethodInfo method in methods)
        {
            Assert(HasAutoChessPostfix(method), message + "：" + method);
        }
    }

    private static CardModel RequireCard(CardModel? card, string message)
    {
        if (card == null)
        {
            throw new Exception(message);
        }
        return card;
    }

    private static int ReadSavedStar(SerializableCard? card)
    {
        int star = 1;
        if (card?.Props?.ints == null)
        {
            return star;
        }

        foreach (SavedProperties.SavedProperty<int> prop in card.Props.ints)
        {
            if (prop.name == AutoChessConfig.SaveKey)
            {
                star = prop.value;
                break;
            }
        }
        return star;
    }

    private static (int Count, int Star) ReadSavedStarStats(SerializableCard? card)
    {
        int count = 0;
        int star = 1;
        if (card?.Props?.ints == null)
        {
            return (count, star);
        }

        foreach (SavedProperties.SavedProperty<int> prop in card.Props.ints)
        {
            if (prop.name == AutoChessConfig.SaveKey)
            {
                count++;
                star = prop.value;
            }
        }
        return (count, star);
    }

    private static void TestSameGroup()
    {
        using var _ = new ModelDbScope();
        var normal = NewCard<StrikeIronclad>();
        var upgraded = NewCard<StrikeIronclad>();
        upgraded.UpgradeInternal();
        var defend = NewCard<StrikeSilent>();
        Assert(StarTracker.IsSameGroup(normal, upgraded), "同 id + 同星、升级不同 => 同组（允许混搭）");
        Assert(!StarTracker.IsSameGroup(normal, defend), "不同卡名 => 不同组");
        StarTracker.Set(defend, 2);
        Assert(!StarTracker.IsSameGroup(normal, defend), "不同星级 => 不同组");
    }

    private static void TestChooseBase()
    {
        using var _ = new ModelDbScope();
        var normal = NewCard<StrikeIronclad>();
        var upgraded = NewCard<StrikeIronclad>();
        upgraded.UpgradeInternal();
        var normal2 = NewCard<StrikeIronclad>();
        CardModel base1 = SynthesisService.ChooseBaseCard(normal, upgraded);
        Assert(ReferenceEquals(base1, upgraded), "升级较高者应被保留为基底");
        CardModel base2 = SynthesisService.ChooseBaseCard(normal, normal2);
        Assert(ReferenceEquals(base2, normal), "升级相同时保留第一张");
    }

    private static void TestScaling()
    {
        using var _ = new ModelDbScope();

        // 普通打击：6 -> 二星 9 -> 三星 18
        var normal = NewCard<StrikeIronclad>();
        SynthesisService.ApplyStarScaling(normal, 2);
        Assert(normal.DynamicVars.Damage.BaseValue == 9m, "打击 二星应 9，实际 " + normal.DynamicVars.Damage.BaseValue);
        SynthesisService.ApplyStarScaling(normal, 3);
        Assert(normal.DynamicVars.Damage.BaseValue == 18m, "打击 三星应 18，实际 " + normal.DynamicVars.Damage.BaseValue);

        // 打击+：9 -> 二星 13 -> 三星 27（升级加成的 +3 也被乘算）
        var upgraded = NewCard<StrikeIronclad>();
        upgraded.UpgradeInternal();
        SynthesisService.ApplyStarScaling(upgraded, 2);
        Assert(upgraded.DynamicVars.Damage.BaseValue == 13m, "打击+ 二星应 13，实际 " + upgraded.DynamicVars.Damage.BaseValue);
        SynthesisService.ApplyStarScaling(upgraded, 3);
        Assert(upgraded.DynamicVars.Damage.BaseValue == 27m, "打击+ 三星应 27，实际 " + upgraded.DynamicVars.Damage.BaseValue);

        // 读档路径（FromSerializable 后 ApplyStarScalingFromBase）：三星打击+ 也应 27
        var load = NewCard<StrikeIronclad>();
        load.UpgradeInternal();
        SynthesisService.ApplyStarScalingFromBase(load, 3);
        Assert(load.DynamicVars.Damage.BaseValue == 27m, "读档三星打击+ 应 27，实际 " + load.DynamicVars.Damage.BaseValue);
    }

    private static void TestScalingIsolation()
    {
        using var _ = new ModelDbScope();

        var a = NewCard<StrikeIronclad>();
        var b = NewCard<StrikeIronclad>();

        SynthesisService.ApplyStarScaling(a, 2);

        Assert(a.DynamicVars.Damage.BaseValue == 9m, "A 卡二星应 9，实际 " + a.DynamicVars.Damage.BaseValue);
        Assert(b.DynamicVars.Damage.BaseValue == 6m, "B 卡不应被 A 的缩放影响，实际 " + b.DynamicVars.Damage.BaseValue);

        // 再缩放一次 A，B 仍然不能动
        SynthesisService.ApplyStarScaling(a, 3);
        Assert(a.DynamicVars.Damage.BaseValue == 18m, "A 卡三星应 18，实际 " + a.DynamicVars.Damage.BaseValue);
        Assert(b.DynamicVars.Damage.BaseValue == 6m, "B 卡仍不应被影响，实际 " + b.DynamicVars.Damage.BaseValue);
    }

    private static void TestIndependentSynthesisCard()
    {
        using var _ = new ModelDbScope();

        var original = NewCard<StrikeIronclad>();
        CardModel? synthesized = SynthesisService.CreateSynthesisCard(original, 2);
        CardModel synthesizedCard = RequireCard(synthesized, "合成卡必须能够创建");

        Assert(!ReferenceEquals(original, synthesizedCard), "合成卡必须是新的 CardModel 实例");

        StarTracker.Set(synthesizedCard, 2);
        SynthesisService.ApplyStarScaling(synthesizedCard, 2);

        Assert(original.DynamicVars.Damage.BaseValue == 6m,
            "新合成卡缩放不应改写原卡，原卡实际 " + original.DynamicVars.Damage.BaseValue);
        Assert(synthesizedCard.DynamicVars.Damage.BaseValue == 9m,
            "独立合成卡二星应为 9，实际 " + synthesizedCard.DynamicVars.Damage.BaseValue);
    }

    private static void TestSavePatch()
    {
        using var _ = new ModelDbScope();
        var mutable = NewCard<StrikeIronclad>();
        StarTracker.Set(mutable, 2);
        SerializableCard? ser = new SerializableCard { Id = ModelDb.GetId(typeof(StrikeIronclad)) };
        CardSavePatches.SavePatch.Postfix(mutable, ref ser);
        int star = ReadSavedStar(ser);
        Assert(star == 2, "保存补丁应写入星级 2，实际 " + star);
    }

    private static void TestSavePatchRecoversScaledCardAfterSl()
    {
        using var _ = new ModelDbScope();
        var mutable = NewCard<StrikeIronclad>();

        // 模拟 QuickSL 的半坏状态：读档已经把伤害恢复为二星 9，
        // 但 RunStarted 随后清空了 StarTracker，导致卡面看起来没有星级。
        SynthesisService.ApplyStarScaling(mutable, 2);
        StarTracker.ClearRunData();
        Assert(StarTracker.Get(mutable) == 1, "清空弱引用后测试卡应暂时看起来是一星");

        SerializableCard? ser = new SerializableCard { Id = ModelDb.GetId(typeof(StrikeIronclad)) };
        CardSavePatches.SavePatch.Postfix(mutable, ref ser);

        Assert(StarTracker.Get(mutable) == 2, "保存前应从已缩放数值恢复二星标记");
        Assert(ReadSavedStar(ser) == 2, "恢复后保存应重新写入 AutoChessStar=2");
        Assert(mutable.DynamicVars.Damage.BaseValue == 9m, "恢复后打击二星伤害仍应为 9");
    }

    private static void TestTitlePatchRecoversScaledCardAfterSl()
    {
        using var _ = new ModelDbScope();
        var mutable = NewCard<StrikeIronclad>();

        // 第一次 SL 后玩家最直观看到的是“数值还在、星星没了”。
        // 标题补丁应先恢复星级，再追加 ★★。
        SynthesisService.ApplyStarScaling(mutable, 2);
        StarTracker.ClearRunData();

        string title = "打击";
        CardTitlePatch.Postfix(mutable, ref title);

        Assert(title == "打击 ★★", "标题应能从二星数值恢复星级，实际 '" + title + "'");
        Assert(StarTracker.Get(mutable) == 2, "标题显示后应把二星标记写回 StarTracker");
    }

    private static void TestRecoveryDoesNotMistakeUpgradedStrike()
    {
        using var _ = new ModelDbScope();
        var upgraded = NewCard<StrikeIronclad>();
        upgraded.UpgradeInternal();
        StarTracker.ClearRunData();

        // 升级打一星击本来就是 9 点伤害，不能把它误认为“未升级二星打击”。
        // 恢复逻辑必须按 CurrentUpgradeLevel 重建一星基准，所以这里不应加星标。
        string title = "打击";
        CardTitlePatch.Postfix(upgraded, ref title);
        Assert(title == "打击", "升级一星卡不应被误判为二星，实际 '" + title + "'");

        SerializableCard? ser = new SerializableCard { Id = ModelDb.GetId(typeof(StrikeIronclad)) };
        CardSavePatches.SavePatch.Postfix(upgraded, ref ser);
        Assert(ReadSavedStar(ser) == 1, "升级一星卡保存时不应写入 AutoChessStar");
    }

    private static void TestDeckVersionEffectiveStar()
    {
        using var _ = new ModelDbScope();

        var deckCard = NewCard<StrikeIronclad>();
        var eventClone = NewCard<StrikeIronclad>();

        // 模拟事件/战斗界面拿到的临时克隆：它自己没有弱引用星级，
        // 只能通过 DeckVersion 回到牌组本体。这里正是之前会退回一星的路径。
        StarTracker.Set(deckCard, 2);
        eventClone.DeckVersion = deckCard;

        Assert(StarTracker.Get(eventClone) == 1, "克隆体自身没有星级记录时应仍是 1 星");
        Assert(StarTracker.GetEffective(eventClone) == 2, "克隆体应能从 DeckVersion 读取到 2 星");

        var secondHopClone = NewCard<StrikeIronclad>();
        secondHopClone.DeckVersion = eventClone;
        Assert(
            StarTracker.GetEffective(secondHopClone) == 2,
            "二跳 DeckVersion 克隆也应回溯到 2 星本体");

        string title = "打击";
        CardTitlePatch.Postfix(eventClone, ref title);
        Assert(title == "打击 ★★", "DeckVersion 克隆标题应显示二星，实际 '" + title + "'");

        SerializableCard? ser = new SerializableCard { Id = ModelDb.GetId(typeof(StrikeIronclad)) };
        CardSavePatches.SavePatch.Postfix(eventClone, ref ser);
        int savedStar = ReadSavedStar(ser);

        Assert(savedStar == 2, "DeckVersion 克隆保存时应写入 2 星，实际 " + savedStar);

        CardModel? fresh = NewCard<StrikeIronclad>();
        CardSavePatches.LoadPatch.Postfix(ser, ref fresh);
        CardModel loaded = RequireCard(fresh, "DeckVersion 克隆读档结果不能为空");
        Assert(
            StarTracker.Get(loaded) == 2 && loaded.DynamicVars.Damage.BaseValue == 9m,
            "DeckVersion 克隆保存后读档应恢复为 2 星 9 伤害，实际 "
            + StarTracker.Get(loaded) + " 星/" + loaded.DynamicVars.Damage.BaseValue);
    }

    private static void TestCreateCloneChainKeepsStar()
    {
        using var _ = new ModelDbScope();
        var deckCard = NewCard<StrikeIronclad>();
        StarTracker.Set(deckCard, 2);
        SynthesisService.ApplyStarScaling(deckCard, 2);

        var directClone = NewCard<StrikeIronclad>();
        SynthesisService.CopyStarToClone(deckCard, directClone);
        Assert(StarTracker.Get(directClone) == 2, "CreateClone 应直接复制 2 星标记");
        Assert(
            directClone.DynamicVars.Damage.BaseValue == 9m,
            "直接克隆后二星打击伤害应为 9，实际 " + directClone.DynamicVars.Damage.BaseValue);

        var eventClone = NewCard<StrikeIronclad>();
        eventClone.DeckVersion = deckCard;

        // 这是更贴近事件链路的情况：事件界面先拿到一张只有 DeckVersion 的克隆，
        // 后续流程又从这张克隆继续复制。如果这里仍只读直接星级，就会退回一星。
        var cloneOfEventClone = NewCard<StrikeIronclad>();
        SynthesisService.CopyStarToClone(eventClone, cloneOfEventClone);
        Assert(
            StarTracker.Get(cloneOfEventClone) == 2,
            "从 DeckVersion 克隆继续复制时，应写入直接 2 星标记");
        Assert(
            cloneOfEventClone.DynamicVars.Damage.BaseValue == 9m,
            "链式克隆后二星打击伤害应为 9，实际 " + cloneOfEventClone.DynamicVars.Damage.BaseValue);
    }

    private static void TestSavePatchDeduplicatesStar()
    {
        using var _ = new ModelDbScope();
        var mutable = NewCard<StrikeIronclad>();
        StarTracker.Set(mutable, 3);

        var starProps = new System.Collections.Generic.List<SavedProperties.SavedProperty<int>>
        {
            new(AutoChessConfig.SaveKey, 2),
        };
        SerializableCard? ser = new SerializableCard
        {
            Id = ModelDb.GetId(typeof(StrikeIronclad)),
            Props = new SavedProperties { ints = starProps },
        };

        CardSavePatches.SavePatch.Postfix(mutable, ref ser);

        var (count, star) = ReadSavedStarStats(ser);

        Assert(count == 1 && star == 3, "保存时应只保留一个 3 星键，实际 count=" + count + " star=" + star);
    }

    private static void TestLoadPatch()
    {
        using var _ = new ModelDbScope();
        var starProps = new System.Collections.Generic.List<SavedProperties.SavedProperty<int>>
        {
            new(AutoChessConfig.SaveKey, 2),
        };
        var ser = new SerializableCard
        {
            Id = ModelDb.GetId(typeof(StrikeIronclad)),
            Props = new SavedProperties { ints = starProps },
        };
        CardModel? fresh = NewCard<StrikeIronclad>();
        CardSavePatches.LoadPatch.Postfix(ser, ref fresh);
        CardModel loaded = RequireCard(fresh, "读档结果不能为空");
        int star = StarTracker.Get(loaded);
        decimal dmg = loaded.DynamicVars.Damage.BaseValue;
        Assert(star == 2 && dmg == 9m, "读档后应为 2 星、9 伤害，实际 " + star + " 星/" + dmg);
    }

    private static void TestLoadPatchDuplicateStars()
    {
        using var _ = new ModelDbScope();
        var starProps = new System.Collections.Generic.List<SavedProperties.SavedProperty<int>>
        {
            new(AutoChessConfig.SaveKey, 2),
            new(AutoChessConfig.SaveKey, 3),
        };
        var ser = new SerializableCard
        {
            Id = ModelDb.GetId(typeof(StrikeIronclad)),
            Props = new SavedProperties { ints = starProps },
        };

        CardModel? fresh = NewCard<StrikeIronclad>();
        CardSavePatches.LoadPatch.Postfix(ser, ref fresh);
        CardModel loaded = RequireCard(fresh, "重复星级键读档结果不能为空");

        Assert(StarTracker.Get(loaded) == 3 && loaded.DynamicVars.Damage.BaseValue == 18m,
            "重复星级键应按最高三星恢复，实际 " + StarTracker.Get(loaded) + " 星/" + loaded.DynamicVars.Damage.BaseValue);
    }

    private static void TestTitlePatch()
    {
        using var _ = new ModelDbScope();
        var mutable = NewCard<StrikeIronclad>();
        StarTracker.Set(mutable, 3);
        string title = "打击";
        CardTitlePatch.Postfix(mutable, ref title);
        Assert(title == "打击 ★★★", "三星标题应带 ★★★ 后缀，实际 '" + title + "'");
    }

    private static void TestSpecialRules()
    {
        using var _ = new ModelDbScope();

        Assert(SynthesisDatabase.IsSpecialEntry("beckon"), "召唤卡必须在 SPECIAL 列表中");
        Assert(SynthesisDatabase.IsSpecialEntry("discovery"), "选择卡必须在 SPECIAL 列表中");
        Assert(SynthesisDatabase.IsSpecialEntry("trash_to_treasure"), "结构卡必须在 SPECIAL 列表中");
        Assert(SynthesisDatabase.IsSpecialEntry("afterlife"), "状态卡必须在 SPECIAL 列表中");

        Assert(
            SynthesisDatabase.GetRule("beckon").Policy == SynthesisCardPolicy.Summoner,
            "beckon 应使用 Summoner 策略");
        Assert(
            SynthesisDatabase.GetRule("discovery").Policy == SynthesisCardPolicy.ComplexChoice,
            "discovery 应使用 ComplexChoice 策略");
        Assert(
            SynthesisDatabase.GetRule("trash_to_treasure").Policy == SynthesisCardPolicy.DeckStructure,
            "trash_to_treasure 应使用 DeckStructure 策略");
        Assert(
            SynthesisDatabase.GetRule("afterlife").Policy == SynthesisCardPolicy.StatefulEvent,
            "afterlife 应使用 StatefulEvent 策略");
        Assert(
            !SynthesisDatabase.ShouldScaleDynamicVar(
                NewCard<Zap>(),
                "Repeat"),
            "球类卡的 Repeat 不应被通用缩放");
    }

    private static void TestEnchantmentCompatibility()
    {
        using var scope = new ModelDbScope();
        var a = NewCard<StrikeIronclad>();
        var b = NewCard<StrikeIronclad>();

        Assert(
            SynthesisService.AreEnchantmentsCompatible(a, b, out _),
            "两张都没有附魔的卡应当兼容");

        var sharpA = ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(typeof(Sharp))).ToMutable();
        var sharpB = ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(typeof(Sharp))).ToMutable();
        a.EnchantInternal(sharpA, 1);
        b.EnchantInternal(sharpB, 1);
        Assert(
            SynthesisService.AreEnchantmentsCompatible(a, b, out _),
            "相同类型、相同数量的附魔应当兼容");

        CardModel? enchantedSynthesis = SynthesisService.CreateSynthesisCard(a, 2);
        Assert(enchantedSynthesis != null, "带附魔卡应能创建独立合成实例");
        Assert(enchantedSynthesis!.Enchantment != null, "合成实例必须保留附魔");
        Assert(enchantedSynthesis.Enchantment!.Amount == 1, "合成实例必须保留附魔数量");

        var c = NewCard<StrikeIronclad>();
        var sharpC = ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(typeof(Sharp))).ToMutable();
        c.EnchantInternal(sharpC, 2);
        Assert(
            !SynthesisService.AreEnchantmentsCompatible(a, c, out _),
            "相同类型但数量不同的附魔不能合成");
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond)
        {
            throw new Exception(msg);
        }
    }

    private sealed class ModelDbScope : IDisposable
    {
        private readonly Type[] _types =
        {
            typeof(StrikeIronclad),
            typeof(StrikeSilent),
            typeof(Zap),
            typeof(Sharp),
        };

        private readonly System.Collections.Generic.List<Type> _injectedByTest = new();

        public ModelDbScope()
        {
            foreach (Type type in _types)
            {
                if (ModelDb.Contains(type))
                {
                    continue;
                }

                ModelDb.Inject(type);
                _injectedByTest.Add(type);
            }
        }

        public void Dispose()
        {
            // 游戏内启动自测时，原版卡牌/附魔通常已经在 ModelDb 中。
            // 这里只移除本次测试临时注入的类型，避免把游戏已加载模型误删掉。
            foreach (Type type in _injectedByTest)
            {
                ModelDb.Remove(type);
            }
        }
    }
}
