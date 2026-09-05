# 自走棋战术 AutoChessTactics —— 设计文档与使用说明

> 适用游戏版本：Slay the Spire 2 `v0.111.0`（2026-08-13 构建，commit `41cef1ea`）
> Mod 版本：0.1.0

---

## 一、玩法总览（三大系统）

这个 Mod 把自走棋（Auto Chess / 金铲铲）的核心循环融进杀戮尖塔2：

| 系统 | 规则 | 触发时机 |
| --- | --- | --- |
| **金币利息** | 每完成一个房间，获得当前金币的 **10%**（向下取整） | 战斗/事件/商店/休息/宝箱房间结束后，回到地图时结算 |
| **商店刷新** | 花费 **20 金币**，把商店里在售的卡牌、遗物、药水全部重新随机 | 商人库存界面右上角新增“刷新商店”按钮 |
| **卡牌合成** | 花费 **20 金币**，把两张同名卡合成更高星级（任一带+结果带+） | 牌组查看界面点“合成”按钮，勾选两张牌 |

### 卡牌合成规则（按你的设计实现）
- 两张**完全相同**的卡（卡名相同、星级相同、升级数相同、均无附魔）→ 合成为一张更高星级的卡；
- **费用（能量）不变**；
- 二星 = 一星效果 × **1.5**（小数向下取整）：例如打击 6 伤害 → 9 伤害；
- 三星 = 二星效果 × **2**：例如二星剑柄打击+ 打19抽3 → 三星打38抽6（即一星的3倍）；
- 合成后卡面上会自动显示 **★★ / ★★★** 后缀，数值也会同步变化；
- 卡牌仍可正常升级（合成后去铁匠铺升级，效果在合成基础上继续增强）；
- 保存/读档后合成结果**不会丢失**（星级写入存档）。

> 数值口径说明：通用规则是“把所有动态变量 ×1.5 / ×2 向下取整”，对所有在白名单内的卡统一生效。
> 因此具体某张卡（如游戏里真正的剑柄打击）的二星数值会按它自己的原始数值算，可能与举例数字略有出入。

---

## 二、如何安装 / 构建

### 直接使用（已安装）
Mod 已安装到 `mods/AutoChessTactics/`。启动游戏后：
1. `设置 -> Mod 设置`，确认“自走棋战术 AutoChessTactics”已启用（新 Mod 默认启用）；
2. 开始一局即可体验：打完房间会看到“利息 +X 金币”提示，战斗/事件后弹出合成界面，进商店右上角有“刷新商店”。

### 重新构建（游戏更新后）
```powershell
powershell -ExecutionPolicy Bypass -File moddev\AutoChessTactics\build.ps1
```
要求：本机 `C:\Users\HP\.dotnet` 下有 .NET 9 SDK（当前环境已具备）。
构建前请先关闭游戏（dll 会被占用）。构建脚本会自动把 dll + manifest 复制到 `mods/AutoChessTactics/`。

### 稳定性自测
离线跑 .NET 9 自测 runner：
```powershell
cd "D:\SteamLibrary\steamapps\common\Slay the Spire 2\moddev\AutoChessTactics"
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project SelfTestRunner\SelfTestRunner.csproj -c Release -- "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

游戏内启动自测：
```powershell
cd "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
$env:AUTOCHESS_SELFTEST='1'
.\SlayTheSpire2.exe
```

通过标准是在 `godot.log` 中看到：
```text
[AutoChessTactics] SelfTest: ALL TESTS PASSED
```

注意：当前 PowerShell 自身运行在 CLR 10，不能直接用 PowerShell 反射测试 Harmony 补丁；
请使用上面的 .NET 9 runner，避免把测试宿主问题误判成 mod 问题。

---

## 三、核心实现说明（给后续维护者）

| 文件 | 作用 |
| --- | --- |
| `AutoChess/AutoChessRunModel.cs` | 主控模型：通过 `ModHelper.SubscribeForRunStateHooks` 注册，记录当前房间；地图打开时结算利息、触发合成 |
| `AutoChess/MapScreenPatch.cs` | Harmony 补丁 `NMapScreen.Open`：房间完成回到地图时回调主控模型 |
| `AutoChess/SynthesisService.cs` | 合成逻辑：校验、扣钱、放大 `DynamicVars`、移除多余卡 |
| `AutoChess/StarTracker.cs` | 用 `ConditionalWeakTable` 把星级挂到每张卡实例上 |
| `AutoChess/CardSavePatches.cs` | 存档补丁：星级写入 `SerializableCard.Props`，读档时恢复数值 |
| `AutoChess/CardTitlePatch.cs` | 标题补丁：卡名后追加 ★★/★★★ |
| `AutoChess/SynthesisOverlay.cs` | 合成界面（纯 Godot 基础控件，无需 pck） |
| `AutoChess/ShopRefreshPatches.cs` | 商店刷新：右上角按钮 + 重新生成 `MerchantInventory` |
| `AutoChess/SynthesisDatabase*.cs` | 可合成白名单 / 待设计名单（由脚本自动生成） |
| `AutoChess/AutoChessConfig.cs` | 所有数值常量（利息、费用等），方便调平衡 |

### 关键设计
1. **数值缩放原理**：杀戮尖塔2 的卡牌数值都放在 `DynamicVars`（动态变量）里，卡面描述与实际结算都读它。
   所以合成时只需把“幸存卡”的每个变量乘以系数，描述和战斗数值会自动同步，连进战斗的克隆体也会继承。
2. **星级存储**：游戏没给 CardModel 预留自定义字段，因此用弱引用表存星级，并借 `SerializableCard.Props`
   （游戏自带的 key-value 存档容器）实现保存/读档。
3. **UI 不依赖 pck**：合成界面与刷新按钮全部用代码创建基础 Godot 控件，Mod 无需打包场景资源，更新游戏后只要重编译 dll 即可。

---

## 四、特殊卡牌规则（147 张）

SPECIAL 卡现在允许合成，但不再统一放大所有 DynamicVars，而是按卡牌类型使用保守规则：

- 复杂选择卡：保留原选择流程，不增加选择次数或分支数量；
- 召唤卡：只放大明确的生命、攻击、伤害、格挡和持续时间，不增加召唤数量；
- 牌组结构卡：生成、移除、复制和抽取等结构动作只执行一次；
- 状态/事件卡：不重复注册监听器或一次性效果；
- 没有可靠数值映射的卡：只提升星级标记，原效果保持不变；
- 充能球卡：基础数值和球数量由独立规则处理。

未登记的新 SPECIAL 卡默认采用“只升星、不改变机制”的安全策略，并写入警告日志，
避免新卡因为未知 DynamicVars 直接导致崩溃。

```
  abundance    afterlife    aggression    alchemize
  anointed    apotheosis    ascenders_bane    ashen_strike
  bad_luck    barricade    beacon_of_hope    beckon
  begone    body_slam    bodyguard    bullet_time
  bully    byrdonis_egg    calamity    calculated_gamble
  capacitor    cascade    chaos    chill
  cleanse    clumsy    conqueror    crescent_spear
  curse_of_the_bell    dark_embrace    darkness    dazed
  death_march    debris    debt    demonic_shield
  deprecated_card    dirge    discovery    distraction
  double_energy    dualcast    eidolon    enlightenment
  enthralled    entrench    expect_a_fight    flanking
  flatten    folly    forbidden_grimoire    frantic_escape
  furnace    fusion    gang_up    gold_axe
  greed    guards    hammer_time    hang
  haunt    havoc    hello_world    hellraiser
  hibernate    hidden_gem    ignition    infernal_blade
  infinite_blades    injury    juggling    lantern_key
  largesse    legion_of_bone    mad_science    malaise
  master_planner    mayhem    memento_mori    mimic
  mind_blast    mirage    multi_cast    murder
  necro_mastery    nightmare    nostalgia    not_yet
  perfected_strike    poke    poor_sleep    precise_cut
  primal_force    prolong    protector    quadcast
  quasar    rainbow    reanimate    reaper_form
  regret    rend    royal_gamble    royalties
  sacrifice    scrawl    secret_technique    secret_weapon
  seeking_edge    severance    snap    soot
  soul_storm    soulbound    splash    spoils_map
  spore_mind    spur    squeeze    stack
  stoke    storm_of_steel    stratagem    subroutine
  summon_forth    supermassive    sweeping_gaze    tempest
  the_hunt    the_sealed_throne    the_smith    times_up
  tools_of_the_trade    tracking    trash_to_treasure    tutor
  tyranny    underworld    unleash    unmovable
  venerate    well_laid_plans    white_noise    wish
  wound    writhe    zap
```

> 提示：上表里标注“状态/诅咒/占位卡”的建议直接保持不可合成（它们本来就不该出现在牌组里当正常卡用）。

---

## 五、可合成卡牌（白名单，449 张，自动生效）

所有白名单内卡牌都会按“二星×1.5 / 三星×2，向下取整”自动缩放数值，无需逐张配置。
完整名单见 `moddev/AutoChessTactics/auto_cards.txt`，主要包括：
- 全部初始牌（打击/防御/痛击/愤怒等）；
- 绝大多数伤害/格挡/抽牌/能量类普通卡与稀有卡（`DynamicVars` 驱动的卡）；
- 施加力量/易伤/中毒等效果的能力卡（`PowerVar` 驱动）。

---

## 六、已知限制（v0.1.0）

1. **附魔卡合成限制**：两张卡必须是相同附魔类型、相同附魔数量；附魔会完整复制，
   但附魔本身不会被重复放大。
2. **多人联机未深度适配**：利息与合成按本地流程实现，优先保证单机体验；联机时以房主逻辑为准，可能有同步差异。
3. **手柄/键盘导航**：合成界面为鼠标优先的简单列表；手柄用户建议用鼠标操作。
4. **特殊卡** 使用逐类安全规则；无法确认数值含义的卡只提升星级，不增加触发次数。
5. 游戏每次更新都可能改动内部 API；若失效，用 `build.ps1` 重新编译即可（必要时我会调整补丁目标）。

---

## 七、后续可扩展方向
- 把利息/合成费用做成可配置（开局菜单或设置项）；
- 为“待设计”卡加入自定义规则后自动归类；
- 加入“连胜/连败利息”“人口/等级”等更多自走棋机制（如需）。



---

## v0.2.0 更新说明

### 1. 合成入口改为“牌组查看界面”按钮
- 不再在战斗/事件房间结束后自动弹出合成界面；
- 打开**牌组查看**（暂停/顶栏“牌组”按钮）后，底部会出现 **“合成 (20金币)”** 按钮；
- 点击后弹出游戏自带的卡牌选择界面（与“融合者”事件相同的交互），**勾选恰好两张**同名卡即可合成；
- 合成/取消后都会正常回到牌组查看，关闭后回地图，可继续前往下一房间。

### 2. 带+规则（任一带+结果就带+）
- 两张卡只需**同名（同 id）+ 同星级**，升级状态可以不同；
- 只要其中一张带 **+**（升级过），合成结果就带 **+**（升级数取较高者），数值按升级版基准放大；
- 例如：普通“打击” + “打击+” → “二星打击+”；“打击+” + “打击+” → “二星打击+”；
  两张“二星打击+” → “三星打击+”。

### 3. AncientWaifus 快捷兼容（可配置开关 CompatAncientWaifus）
- 修复 AncientWaifus 因引用旧版 `SetAnimation` 导致的**每次输入崩溃**（短路其输入入口 `GlobalInputCatcher._Input`）；
- 其皮肤/贴图替换功能保留，触摸/点击互动被禁用；
- 兼容补丁在**所有 Mod 加载完成后**自动应用（Mod 加载顺序不固定，延迟应用确保目标类型已存在）。

### 4. 已知说明：KaguyaSilentRavenSkin 等其它同类 Mod
- `KaguyaSilentRavenSkin` 等 Mod 也存在相同的旧版 `SetAnimation` 问题；
- 这类“方法体直接引用旧 API”的崩溃**无法用 Harmony 修复**（Harmony 复制原方法 IL 时同样会解析失败），
  只能在其触发场景（如角色选择时的点击互动）报错一次，游戏本身可继续；
- 如果被反复报错困扰，建议在 **设置 -> Mod 设置** 里暂时禁用这些皮肤 Mod，或等作者更新。


---

## v0.3.0 更新说明

### 1. 合成数值修复（三星 = 一星含升级 × 3）
- 合成数值改为从【1 星含升级基准】直接乘累计系数（二星 ×1.5、三星 ×3，向下取整），
  升级加成的数值也会被一并乘算；
- 例：打击 6 → 二星 9 → 三星 18；打击+ 9 → 二星 13 → 三星 **27**（不再是 18+3 或 26）；
- 读档恢复与合成使用同一套数值口径，两者结果一致。

### 2. 生成球类卡（Defect 充能球卡）可合成 + 球数增加
- 球状闪电 / 寒冰之触 / 冷静头脑 / 冰川 / 闪电 / 黑暗 / 寒冷 / 风暴 / 混乱 全部可合成；
- 球数量：**固定+1**（每升一星多 1 个球）——闪电 1→2→3、冰川 2→3→4、寒冷=敌人数量+星-1；
- **X 费牌（风暴）**：总球数 = X × 2^(星-1)，即一星 1X、二星 2X、三星 4X；
- 混乱的“重复”变量不会被数值缩放重复计算（球数统一按固定+1 规则）。

### 3. 战斗结束卡死修复
- 修复星级存档在写战斗回放/多人序列化时因未知属性名（AutoChessStar）无 netID 映射而抛异常
  导致战斗结束卡住、回主界面卡住的问题；
- 本地 JSON 存档不受影响（星级照常保存/读档），仅回放/多人序列化跳过星级字段。

### 4. 合成流程简化 + 按钮 UI
- 点“合成”按钮后【自动关闭牌组预览】并切换到合成选择画面；
- 合成完成或取消后【自动重新打开牌组查看】，可继续合成其它牌；
- 合成按钮改为醒目的**黄色**。

---

## v0.4.0 更新说明

### 1. 修复宝箱房间黑屏
- `AfterRoomEntered` 不再在房间切换调用栈内直接发放利息；
- 等待旧房间 UI 释放、新房间完成初始化且转场结束后再调用原生金币命令；
- 访问已销毁节点时自动重试，部分玩家已成功发放的利息不会重复领取。

### 2. 修复商店刷新信号重复连接
- 不再重复调用 `NMerchantInventory.Initialize`；
- 使用现有槽位的 `FillSlot` 重填卡牌、遗物和药水；
- 刷新失败会恢复原库存并退还费用，连续刷新不会重复连接 `Hovered/Unhovered`。

### 3. 开放 SPECIAL 与附魔卡合成
- SPECIAL 卡全部进入合成选择流程，并按复杂选择、召唤、牌组结构、状态/事件逐类处理；
- 附魔卡要求类型和数量完全一致，合成后保留完整附魔；
- 星级缩放会先恢复基础卡数值，再只应用一次附魔逻辑，避免附魔效果重复叠加。
