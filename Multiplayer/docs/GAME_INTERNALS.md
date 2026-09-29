# Game internals (0.2.16a5)

Facts about the game code that the MP design relies on. Paths are relative to the decompiled source in `reference/decomp/0.2.16a5/` at the repository root (regenerate with `scripts/decompile.ps1`). Mark each entry **verified** (observed at runtime) or **read** (from decompiled code only). Update after every game update.

## Runtime

- Unity 6 (`UnityEngine.Modules` 6000.0.65 compiles), Mono scripting backend, x64. BepInEx 5.4.23.x with HarmonyX in `BepInEx/core`. **read**
- `Assembly-CSharp.dll` ≈ 400 types / 53k decompiled lines; no Steamworks, no networking libraries. **read**
- `System.IO.Compression.dll` present; `MonoPosixHelper.dll` present in `MonoBleedingEdge/EmbedRuntime` (DeflateStream works). **read**
- Saves: `<persistentDataPath>/Saves/*.txt` (`%USERPROFILE%/AppData/LocalLow/CoffeeHeaven/Processor Tycoon Beta/Saves`), plain JSON; late-game saves ≈ 14.5 MB. Shared by all game copies of the same user. **verified**

## Time — `ProcessorTycoon.TimeSystem/DateController.cs`

- `Update()`: `seconds += Time.deltaTime * currentTimeSpeed`; at ≥ 1 → `TriggerTicks()` (one in-game day). **read**
- `TriggerTicks()` order: `OnEarlyTick`, `OnAIBehaviourTick`, `OnPreContractSellTick`, `OnClientProductionTick`, `OnContractProductionTick`, `OnContractSellTick`, `OnContractTick`, `OnProductionTick`, `OnMarketSellTick`, `OnTick`, `OnLateTick`, `OnDayPassed`; on day 1: `OnCompanySpawnTick`, `OnMonthCacheUpdate`, `OnEarlyMonthPassed`, `OnMonthPassed`; Jan 1: `OnYearPassed`; day 1: `OnLateMonthPassed`; Jan 1: `OnLateYearPassed`, then yearly `SaveHandler.Autosave()`. **read**
- `NextMonth()` and `SimulateUntilYear()` loop `TriggerTicks()` synchronously (usable for catch-up). **read**
- Pause: `PauseHandler.Update()` calls `DateController.ManualPause/ManualResume` whenever any active `PauseTrigger` (dialog/form window) exists. **read**
- Only non-deterministic time source in simulation code: none found; `DateTime.Now` only in `TimeSystem.PlayerUI/CurrentTime.cs` (UI clock). **read**

### Tick subscribers (first classification — complete in spike S3)

| Event | Company-scoped sim | World/shared sim | UI |
|---|---|---|---|
| OnEarlyTick | TeamHiringHandler?, FactoryUpgradeHandler? | InflationHandler, ContractManager (contract generation, RNG), CpuUnitCostUpdater, WaferCostUpdater, TechnologyTimeUpdater | ProductionWindow |
| OnAIBehaviourTick | AIBehaviourController (per AI company) | | |
| OnPreContractSellTick | CompanyFinance | ContractSeller | |
| OnClientProductionTick / OnContractProductionTick / OnProductionTick | Company, AICompany (factory; includes foundry work for other companies' CPUs) | | |
| OnContractSellTick | | ContractSeller | |
| OnMarketSellTick | | Market (sells all CPUs, writes stock/sales into every CPU) | |
| OnTick | Company, AICompany (research sector update) | CentralBank, WaferCostUpdater, TimerScheduler (ticks **all** projects of all companies) | |
| OnLateTick | Company, AICompany | CreatorFactoryExpansion? | ContractManagementWindow, Tooltip, MarketTopBar |
| OnDayPassed | Company, AICompany, ContractAutomation (player) | BankruptcyHandler, BusinessContractManager, CompanyEventHandler, CompanyTypeUpdater | Business/Contract/Market/Production/Research windows, CpuSalesUI, AllProjectsButton |
| OnCompanySpawnTick / FirstEverTick | | CompanySpawner | |
| OnMonthCacheUpdate | | CentralBank, CpuDataProvider, FactoryUpgradeHandler | |
| OnEarlyMonthPassed | | TaxationHandler, WaferCostUpdater, MarketDataHandler, MarketDataProvider | ContractManagementWindow, ContractWindow |
| OnMonthPassed | Company, AICompany | CompanyEventHandler, ArchitectureManager | BusinessWindow, FinanceGraphWindow, InspectorWindow |
| OnLateMonthPassed | AIBehaviourController, AI, CompanyFinance, Player | Market (cache CPUs/competitors), CompanyDataProvider | Calendar, graphs, spreadsheets, HardwareWindow, MarketShareWindow, CpuSalesUI |
| OnYearPassed / OnLateYearPassed | CompanyFinance | CompanyEventHandler, ContractManager | EmailWindow, graphs, ResearchProjectWindow, EndGameWindow |

Company handlers are closures in `Company.Initialize*` / `AICompany.Initialize*` (e.g. `AICompany.InitializeResearchSector`, `InitializeFactory`), so their delegate `Target` is the company instance (basis of D19). `TimerScheduler.PassDay` iterates a shared `timers` list → needs per-item filtering. **read**

## Save / load — `ProcessorTycoon.Save/`

- Email: `EmailWindow` (singleton) keeps `List<Email> emails`; `InstantiateButton(email)` adds a list card, `UpdateEmailText(email)` shows one (title, `authorText` = the player's founder, content), `UpdateNotification()` sets the desktop badge (it runs in FixedUpdate only while the window is active, so call it after changes). An `Email` is initialised from an `EmailMessage` (ScriptableObject: Title, Text); a save keeps only `EmailID`, `HasBeenRead` and `Date` and rebuilds ID 0 (the only native kind, the year summary) from its message, so other emails load back empty. `GetEmails()` is called only by `SaveHandler.Save`. The window ships inactive `AcceptButton`/`DeclineButton` (Background/BackgroundRight) that no game code uses. `Desktop.Email/*.cs`, `Save/SaveHandler.cs`. **verified**
- `SaveHandler.Save(name)` builds `SaveObject`: player company + all `AICompany` (via `FindObjectsByType`), each company's CPUs, custom hardwares, projects from `TimerScheduler` (CPU, factory expansion, research, custom hardware), continuous projects, available/active contracts, business contracts, emails, market (potential sales, architecture market data + owner IDs, PGA event), player balance, player preferences, `IdCount`. Then `JsonUtility.ToJson` → `SaveSystem.Save` (file). **read**
- `SaveHandler.Load(file)` → `SceneTransitionHandler.LoadSceneFromSave(3f, callback)`: scene reload with a 3 s transition, then unpack in order: date, IDs, player prefs, companies (`SaveObjectInstantiator.UnpackFromSave`), custom hardwares, CPUs (`InstantiateFromSave` + `company.AddCpu`), `DuringLoad`, projects, continuous projects, contracts, business contracts, …, events `OnLoaded`, `OnLateLoaded`, `OnLastLoadedTick`. **read**
- `DataConverter.Convert<T>(obj)` = `JsonUtility.FromJson<T>(JsonUtility.ToJson(obj))`: live model classes and save DTOs share field shapes. Model classes are plain classes with `[field: SerializeField]` auto-properties (e.g. `Hardware/CpuBase.cs` `MarketData`). References (`Company`, `Architecture`, `Package`, `ProcessNode`, `Memory`, `Base`) are re-resolved by ID in `SaveObjectToCpu`. **read**
- `UnpackFromSave(company)` appends to lists (`company.Teams.Add`) and calls `Load*` methods — not idempotent; never call it on an existing company. **read**
- CPU market data is trimmed on save (`DataHelper.TrimCpuData`) and untrimmed on load (`UntrimCpuData`). **read**
- Vanilla load options list only top-level `*.txt` in the save folder (`SaveSystem.LoadOptions`). **read**

## Companies — `ProcessorTycoon.CompanySystem/`

- Player company: `Company` (MonoBehaviour), `IsPlayer => true`; owner is the `Player` singleton (`ProcessorTycoon/Player.cs`), referenced as `Player.Instance` 258 times in 64 files. **read**
- AI companies: `AICompany`, `IsPlayer => false` (`AICompany.cs:83`). **read**
- `CompanySpawner.SpawnCompanyFromSave(uniqueID, saveID)`: returns for `"PLAYER"`/empty; otherwise spawns from a matching `HistoricalCompany` template — **no match → null reference**. Ghost companies need a patched spawn path (spike S6). **read**
- `IsPlayer` branches in simulation: contracts (`ContractSystem/Contract.cs:205`, `ContractHelper.cs:91`, `ContractManager.cs:97,107`), `Production/CreatorFactoryExpansion.cs:154`, `CompanyHelper`, `CompanyDataProvider`, `AICompany.cs:372,522`. **None in `MarketSystem`.** **read**

## IDs — `ProcessorTycoon.Save/SaveIDHandler.cs`

Plain counter `instances`; `NewID()` returns and increments; `LoadID(id)` sets `id + 1`; static scene objects get the first IDs in `Awake`. **read**

## Randomness

~28 call sites: `UnityEngine.Random` in `AISystem/*` and `AICompany.cs:479,518`; unseeded `System.Random` in `ContractSystem/ContractManager.cs:19,34`, `Contract.cs`, `ContractInstantiator.cs`; audio. None found in `MarketSystem` (verify in S4). **read**

## Player actions live in UI code (no command layer)

Examples: `ProjectSystem/ProjectReleaseWindow.cs` `ReleaseCpu()` reads `priceInput.text` and sets `currentCpu.Price/IsReleased`, calls `company.AddCpu`; `MarketSystem.UI/CpuEditWindow.cs` `ApplyChanges()`; `CompanySystem.PlayerUI/CompanyEditWindow.cs` `ApplyChanges()`. Buttons are mostly wired in scene/prefab data (only ~18 `AddListener` calls in code). Irrelevant for owned state under D3; relevant for the few shared-object commands. **read**

## Other singletons (reference counts)

`DateController.Instance` 225, `Market.Instance` 74, `SaveHandler.Instance` 60, `BusinessContractManager.Instance` 52, `TimerScheduler.Instance` 50, `CreatorManager.Instance` 37, `CpuDataProvider.Instance` 34, `ContractManager.Instance` 23. **read**

## M0 spike results (2026-09-27, testbed A, fixture `mpfx-mid-1995`) — **verified**

- **S2 (part 1):** two game copies (testbeds A and B) run simultaneously on one PC; no single-instance lock; each Agent bridge on its own port. `Application.version` reports `0.2.0`, not `0.2.16a5`: the handshake must use `SaveHandler`'s hard-coded save version string + `Assembly-CSharp` hash, not `Application.version`.
- **S1 JsonUtility semantics:** keys of `[field: SerializeField]` auto-properties are `<Name>k__BackingField`. `FromJsonOverwrite` on a nested `[Serializable]` object **merges in place** (same instance, absent keys kept); on a `List<T>` it **replaces the contents in the same list instance**. Full round trip of a live `Cpu` is byte-identical. `JsonUtility.ToJson(liveCpu)` contains `{"instanceID":…}` for `Architecture`/`ProcessNode`/`Memory` (machine-specific; never send). The save DTO `DataConverter.CpuToSaveObject(cpu)` has no instanceIDs, applies onto a live `Cpu` with `FromJsonOverwrite` (DTO-only keys such as `CompanyID`, `Architecture` string are ignored because live keys differ) and the live object re-serializes to exactly the applied DTO. A partial company DTO (`{"Money":…,"Factory":{"<ProductionCapacity>k__BackingField":…}}`) merges into `SaveObject.Company` keeping the nested instance. Live `Factory` round trip keeps `Factory.Company`.
- **S3 handler inventory:** every `DateController`/`SaveHandler` handler target resolves to one of: the player `Company` (closures `Company.<Initialize*>`), an `AICompany` (closures), components on the AI company GameObject (`AI`, `AIBehaviourController` via display class, `CompanyFinance`), `Player`, a world system MonoBehaviour, a UI MonoBehaviour, or one static lambda (`<>c.<Start>b__7_1` in `OnEarlyTick`). AI company GameObject components: `Transform, AICompany, AI, AIBehaviourController, CompanyFinance`. World systems: FactoryUpgradeHandler, ContractManager, InflationHandler, CpuUnitCostUpdater, TeamHiringHandler, TechnologyTimeUpdater, CentralBank, WaferCostUpdater, TimerScheduler (`PassDay`), ContractSeller, Market, BankruptcyHandler, CompanyEventHandler, CreatorFactoryExpansion, BusinessContractManager, CompanyTypeUpdater, ContractAutomation, CompanySpawner, CpuDataProvider, MarketDataHandler, MarketDataProvider, TaxationHandler, ArchitectureManager, CompanyDataProvider, Cheats. `TimerScheduler.timers` holds only `Project` instances in this save (`ITimer.Tick` is an `Action` property). Full log: rerun spike `S3-inventory`.
- **Player company** is the only static SaveID instance (`Player & Company`, SaveID **0**, UniqueID `PLAYER`). `SaveObjectInstantiator.UnpackFromSave` finds the player company by the saved SaveID, so a save whose player company has another SaveID needs the live player company's SaveID set first (S7 approach).
- **RNG call sites** are only in `AISystem/AIBehaviour*`, `AICompany` (fabless decision) and `ContractSystem` (`Contract`, `ContractInstantiator`, `ContractManager`). Market, bank, inflation, production, research, business, company events and spawner are deterministic.
- **S5 cost (1995, 12 companies, 102 CPUs):** company DTOs 67 ms total (player DTO 257 KB: research sector 27 KB, factory 23–27 KB, rest mostly financial reports); 22 on-sale CPU DTOs 10 ms / 291 KB; whole world 114 entities 4.5 MB 119 ms incl. FNV-1a.

## Projects, research and speed buttons (2026-09-27, D52)

- `TimerScheduler` holds `Project` (CPU, factory expansion, `ResearchProject` = research *modifier* projects, custom hardware) and `ProjectContinuous`; `Project.Company` / `ProjectContinuous.CompanyID` name the owner. `Get*Project()` return the live DTOs with `Progress`/`IsPaused` refreshed; `SaveObjectInstantiator.InstantiateFromSave(dto)` works for any company (UI only for the player); `TimerScheduler.Unschedule` removes. `ProjectSystem/Project.cs`, `Save/SaveObjectInstantiator.cs`, `Save/SaveHandler.cs`. **verified**
- Technology research is not a project: `ResearchSector.researches[].Progress` and `teamOne.CurrentTechnologyID` (current selection) are company state. The AI clears the selection and sets its own funding when it plays a company. **verified**
- `SaveHandler.Save` serializes the finished `SaveObject` with `JsonUtility.ToJson(obj, prettyPrint)` (the D52 hook). **verified**
- Speed buttons are `SelectableButton`s; `SelectableButtonsHandler.CurrentButtonWithID(id)` is the selection registry. `Select()` is a no-op for a button already in the Selected state, so after a load the registry can be empty while the button looks selected; `SetCurrentButtonWithID` fixes it. `UI/SelectableButton.cs`, `UI/SelectableButtonsHandler.cs`. **verified**
- `AICompany.Initialize` → `InitializeResearchSector` → `ResearchSector.UnlockTechnologies(initialData.GetTechnologyYear())`: a company spawned from a historical template unlocks that template's technologies; `AddTechnology` appends to derived lists (`WaferSizes`, `ProcessNodes` + `NodeOptimizations`, ...) that are never rebuilt. `FactoryUpgradeHandler` (world system, `OnEarlyTick`, every company) upgrades lines toward `company.WaferSizes.Last()` and charges `UpgradeCost` as construction daily. `AICompany.cs`, `Production/FactoryUpgradeHandler.cs`, `Production/Factory.cs`. **verified**
- `SaveObjectInstantiator.InstantiateToSave(company)` sets `Divisions = company.Divisions` (the live object) and `UnpackFromSave` assigns `company.Divisions = saved.Divisions`: a DTO cloned from a live company shares its `Divisions` (D54). Other members are copied (`DataConverter.Convert` JSON round trips). `CompanyTypeUpdater.TriggerUpdate` recomputes `Divisions.Manufacturing` of every company from `Factory.ProductionCapacity`. `SaveObjectInstantiator.cs`, `DataConverter.cs`, `CompanyTypeUpdater.cs`. **verified**
- Pausing without `PauseHandler`: `BankruptcyWarningWindow.OnDisable` and `ResearchCompletedWindow` ("Select New Research") call `DateController.ResumeAndOverride(0)` = `ManualPause`. `TimeController` maps the keyboard to `ManualSetTimeSpeed`. `BankruptcyWarningWindow.cs`, `ResearchCompletedWindow.cs`, `TimeController.cs`. **verified**
- AI decisions of an `AICompany` live in `ProcessorTycoon.AISystem` components: `AIBehaviourController` subscribes `OnAIBehaviourTick` (finance, research choice, production, research projects, market, business, sockets, new CPU designs, factory expansion) and `OnLateMonthPassed` (contracts, business, market). Simulation (production, research progress, operating cost, projects) is on the company and `TimerScheduler`. `AIBehaviourController.cs`, `AICompany.cs`. **read**
- Bankruptcy: `BankruptcyHandler.TickBankruptcies` (daily, every company): default when money < −`CentralBank.DebtLimit`; after `defaultTimeLimit` days AI companies get `BankruptcyWarning`, then `Bankrupt` (`TriggerBankruptcy`; for the player `BankruptcyWindow.Open`, the Game Over with "Return To Menu") or `CancelDefault`. `Company.TriggerBankruptcy` only sets `IsBankrupt`; `AICompany.TriggerBankruptcy` also unschedules projects and retires CPUs. After bankruptcy the top-bar text stays "Bankruptcy in 0 days" (`UpdateUI` runs only in default). `BankruptcyHandler.cs`, `BankruptcyWindow.cs`, `Company.cs`, `AICompany.cs`. **verified**
- New game: `MenuGameSetup` (main menu) builds `PlayerInitialData`: funds and factory size from a per-difficulty table indexed by start date, ×5 funds for fabless and foundry, ×0 / ×5 lines; `StartingTechnology` per difficulty; Impossible allows only CPU companies. Start calls `GameManager.StartNewGame` → `Player.InitializeAsNewGame` → `Company.InitializeAsNewGame` (unlock technologies up to start year ± the StartingTechnology offset, `Factory.InitializeLineGroups` puts every line on the newest wafer, node optimizations complete). `CompanyFinance.LoadReports` needs at least one report. `MenuGameSetup.cs`, `GameManager.cs`, `Company.cs`, `Factory.cs`, `CompanyFinance.cs`. **verified**
- UI resources: `ThemeManager.Instance.CurrentTheme` (`Theme`: Background, TopBarBackground, Text, ButtonBackground, CTAButtonBackground, InputBottomLine…, ScrollBarHandle, SpreadsheetColor1/2, IsDark), `OnThemeChanged`; windows use RoundCorners42 (pixelsPerUnitMultiplier: window 3, button/dropdown/toggle 5, scrollbar 4, input 10), a 1 px #00000099 outline, the `shadow` sprite and a 30 px top bar; `PopupManager.InstantiateGenericNotification(message, secondary, duration)` shows a native popup; `TooltipTrigger` + a `TooltipData` (Header, Content, Delay, PreferedWidth) shows a native tooltip; `CursorController.SetCursorType`. `Theme.cs`, `ThemeManager.cs`, `PopupManager.cs`, `TooltipTrigger.cs`, `TooltipData.cs`, `CursorController.cs`. **verified**
