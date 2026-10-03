# Changelog

## [1.7.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.6.2...v1.7.0) (2026-10-03)


### Features

* **ai:** prefer a recognizable weekly rhythm in plan proposals ([5084943](https://github.com/somali-lab/keep-the-house-clean-planner/commit/508494323193a1620815de0ff2dfe611beb9728d))
* **badges:** let admins define badges awarded from executions ([f261b40](https://github.com/somali-lab/keep-the-house-clean-planner/commit/f261b40c98a80eddbb290fc7e897ab3151240db8))
* **occurrences:** record extra executions of a task, also several times a day ([02d00b1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/02d00b168b03c19b0a350b17f3eb3c2c28554b72))
* **occurrences:** record one-off tasks without a task record ([02d00b1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/02d00b168b03c19b0a350b17f3eb3c2c28554b72))
* **planner:** open AI drafts in plan management after creation ([5084943](https://github.com/somali-lab/keep-the-house-clean-planner/commit/508494323193a1620815de0ff2dfe611beb9728d))
* **planner:** preview plan activation effects ([#52](https://github.com/somali-lab/keep-the-house-clean-planner/issues/52)) ([715c7a1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/715c7a12aa66fc9a646ae41455a74f1fb7a1ddfb))
* **planner:** search tasks and show four-week workload totals ([d98a6fb](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d98a6fb517765f046d040fc8c73c78dbe5056d9f))
* **points:** award points for past executions and show balances per person ([384405b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/384405b05de0772f715471a1f06f045fab143bfe))
* **points:** award week and cycle bonuses ([d8f1d98](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d8f1d980ec70cd4d0afddd6eb1989a5e64ab7d40))
* **points:** convert points to money and book redemptions ([340a324](https://github.com/somali-lab/keep-the-house-clean-planner/commit/340a324721572a6ea3d8b8b85261f7a725b5755c))
* **points:** default to one point per minute and let one-off tasks carry points ([384405b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/384405b05de0772f715471a1f06f045fab143bfe))
* **points:** record points per execution in a ledger ([384405b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/384405b05de0772f715471a1f06f045fab143bfe))
* **reward:** add a reward meter with a chicken and eggs ([4b8ec30](https://github.com/somali-lab/keep-the-house-clean-planner/commit/4b8ec30aa887f0dad076cfe80c6f4825e2764366))
* **tasks:** group My tasks into dated sliding weeks ([d98a6fb](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d98a6fb517765f046d040fc8c73c78dbe5056d9f))
* **today:** add a dialog to record extra work and one-off tasks ([02d00b1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/02d00b168b03c19b0a350b17f3eb3c2c28554b72))
* **today:** plan extra tasks and rename the dialog to Extra task ([#71](https://github.com/somali-lab/keep-the-house-clean-planner/issues/71)) ([96d9cef](https://github.com/somali-lab/keep-the-house-clean-planner/commit/96d9ceff21de6853794de27bcb0dedd4503c63a3))
* **today:** show everyone's tasks in two columns on wide screens ([2f1192f](https://github.com/somali-lab/keep-the-house-clean-planner/commit/2f1192f7673311ed1944b62649c48265d5dac1df))
* **transfer:** export schema version 2 and reject duplicate keys before importing ([02d00b1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/02d00b168b03c19b0a350b17f3eb3c2c28554b72))
* **web:** add a Home button that returns from management to the week overview ([2f1192f](https://github.com/somali-lab/keep-the-house-clean-planner/commit/2f1192f7673311ed1944b62649c48265d5dac1df))
* **web:** add an About page with release information ([#59](https://github.com/somali-lab/keep-the-house-clean-planner/issues/59)) ([ad1450d](https://github.com/somali-lab/keep-the-house-clean-planner/commit/ad1450db9c55f0e2aa20aff4c0cac8ce8c2d34ce))
* **web:** add per-person browser notifications while the planner is open ([#62](https://github.com/somali-lab/keep-the-house-clean-planner/issues/62)) ([3f7bdf3](https://github.com/somali-lab/keep-the-house-clean-planner/commit/3f7bdf35d89e5c437939c190b79b8db4238a17dc))
* **web:** persist filters per profile across views ([d98a6fb](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d98a6fb517765f046d040fc8c73c78dbe5056d9f))
* **week:** search tasks and toggle cycle-week labels ([d98a6fb](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d98a6fb517765f046d040fc8c73c78dbe5056d9f))


### Bug fixes

* **badges:** keep badge rules valid when tasks are deleted ([f261b40](https://github.com/somali-lab/keep-the-house-clean-planner/commit/f261b40c98a80eddbb290fc7e897ab3151240db8))
* **context-maintainer:** allow direct instruction files ([#43](https://github.com/somali-lab/keep-the-house-clean-planner/issues/43)) ([019e194](https://github.com/somali-lab/keep-the-house-clean-planner/commit/019e194087c85df59c040c60fe6638528731be24))
* **context-maintainer:** run only after releases ([69b39c7](https://github.com/somali-lab/keep-the-house-clean-planner/commit/69b39c79d98a296d3111e742a61fe7cea8374126))
* **occurrences:** require an explicit choice when completing someone else's task ([384405b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/384405b05de0772f715471a1f06f045fab143bfe))
* **planner:** clarify draft visibility in task overviews ([#51](https://github.com/somali-lab/keep-the-house-clean-planner/issues/51)) ([66bc0cd](https://github.com/somali-lab/keep-the-house-clean-planner/commit/66bc0cdf26e4b33a9a3cf40cee102d209369d216))
* **planner:** clear the draft flag when a draft plan is activated ([5084943](https://github.com/somali-lab/keep-the-house-clean-planner/commit/508494323193a1620815de0ff2dfe611beb9728d))
* **points:** keep bonuses fair when others finish overdue work ([d8f1d98](https://github.com/somali-lab/keep-the-house-clean-planner/commit/d8f1d980ec70cd4d0afddd6eb1989a5e64ab7d40))
* **points:** keep redemptions safe across currency changes and old imports ([340a324](https://github.com/somali-lab/keep-the-house-clean-planner/commit/340a324721572a6ea3d8b8b85261f7a725b5755c))
* **reward:** play the celebration once and keep the meter current ([4b8ec30](https://github.com/somali-lab/keep-the-house-clean-planner/commit/4b8ec30aa887f0dad076cfe80c6f4825e2764366))
* **server:** migrate the occurrence slot index to generated occurrences only ([02d00b1](https://github.com/somali-lab/keep-the-house-clean-planner/commit/02d00b168b03c19b0a350b17f3eb3c2c28554b72))
* **server:** remove AI draft activation that skipped the preview ([0ec9e2e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/0ec9e2eb72ea89a2fb126cc4d0a04f4f6f3967b2))
* **settings:** explain which weeks and cycles a bonus schedule covers ([340a324](https://github.com/somali-lab/keep-the-house-clean-planner/commit/340a324721572a6ea3d8b8b85261f7a725b5755c))
* **web:** keep the planner toolbar on one line and reset history from the header ([#70](https://github.com/somali-lab/keep-the-house-clean-planner/issues/70)) ([5c3ab50](https://github.com/somali-lab/keep-the-house-clean-planner/commit/5c3ab5090ae35b7a4801c28b2c8bb0f49b7eab73))
* **web:** move filter reset to one header icon per screen ([#69](https://github.com/somali-lab/keep-the-house-clean-planner/issues/69)) ([5bb8623](https://github.com/somali-lab/keep-the-house-clean-planner/commit/5bb8623ef1fb629c12b5065e8557feff1e7ff49a))


### Build system and dependencies

* **deps-dev:** bump the npm-development group with 2 updates ([#56](https://github.com/somali-lab/keep-the-house-clean-planner/issues/56)) ([6ae0d2a](https://github.com/somali-lab/keep-the-house-clean-planner/commit/6ae0d2a2d8fe916abf954f653cf0f153dd1d40ff))
* **deps-dev:** update compatible development tools ([e727ed9](https://github.com/somali-lab/keep-the-house-clean-planner/commit/e727ed9549d6e14276206177dbdf5d033329867f))
* **deps:** bump the github-actions group with 2 updates ([#47](https://github.com/somali-lab/keep-the-house-clean-planner/issues/47)) ([cc11b92](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cc11b920143a9ea2c9abaf1f385d82c00f8143b9))
* **deps:** bump the npm-production group with 8 updates ([#49](https://github.com/somali-lab/keep-the-house-clean-planner/issues/49)) ([639ac2e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/639ac2ef59dee8225b8b670821c795bb7b08595b))
* isolate incompatible development updates ([e727ed9](https://github.com/somali-lab/keep-the-house-clean-planner/commit/e727ed9549d6e14276206177dbdf5d033329867f))


### Continuous integration

* add a docs maintainer that keeps requirements and README current ([#73](https://github.com/somali-lab/keep-the-house-clean-planner/issues/73)) ([0de45e4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/0de45e4ddb3bb1d253e69cc529eebe64b6a0ffe9))
* cache test browser and MongoDB binary in the server job ([#57](https://github.com/somali-lab/keep-the-house-clean-planner/issues/57)) ([fb9bc99](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fb9bc9966ebfe71654df41eb0aa03317b4310dac))


### Documentation

* add agent model allocation and unattended loop to the wishlist plan ([#68](https://github.com/somali-lab/keep-the-house-clean-planner/issues/68)) ([7f61fad](https://github.com/somali-lab/keep-the-house-clean-planner/commit/7f61fadf78f0b16bedd049285301d6a276f89d47))
* add agent-executable wishlist implementation plan ([cd694f6](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cd694f602207f64d457db61ccdb89b3f74f5b844))
* **adr:** keep only architecture choices in ADR-0009 to ADR-0015 ([#72](https://github.com/somali-lab/keep-the-house-clean-planner/issues/72)) ([30aefa6](https://github.com/somali-lab/keep-the-house-clean-planner/commit/30aefa6e93e4f84c65ac3412f89e4953442d6ca9))
* **agentics:** document context checks ([69b39c7](https://github.com/somali-lab/keep-the-house-clean-planner/commit/69b39c79d98a296d3111e742a61fe7cea8374126))
* **agents:** require documentation review before pull requests ([cd694f6](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cd694f602207f64d457db61ccdb89b3f74f5b844))
* align requirements and README with the current behaviour ([#74](https://github.com/somali-lab/keep-the-house-clean-planner/issues/74)) ([9b5a23e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/9b5a23ebeb74bd76ab7be38076628758894a41b9))
* index scoped instructions in AGENTS.md ([#46](https://github.com/somali-lab/keep-the-house-clean-planner/issues/46)) ([71d560c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/71d560cb6d837b496ed09cb4d794c1a79a3964bb))
* **shared:** correct stale points and budget comments ([0ec9e2e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/0ec9e2eb72ea89a2fb126cc4d0a04f4f6f3967b2))

## [1.6.2](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.6.1...v1.6.2) (2026-09-28)


### Continuous integration

* **context-maintainer:** look for missing instruction files ([#42](https://github.com/somali-lab/keep-the-house-clean-planner/issues/42)) ([5148269](https://github.com/somali-lab/keep-the-house-clean-planner/commit/51482692ad1bd6d91c6672dee0fad060e8bf0956))


### Tests

* add a Playwright script that captures the README screenshots ([cad3796](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cad3796dc532e34f4699139360c3d814b683abe8))


### Documentation

* add architecture decision records ([86e740e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/86e740ede410f00b4b0ea516f46c237c2bce71ab))
* correct the release and branch-naming documentation ([86e740e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/86e740ede410f00b4b0ea516f46c237c2bce71ab))
* define how the working documents are used ([#39](https://github.com/somali-lab/keep-the-house-clean-planner/issues/39)) ([bf44d2e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/bf44d2ec436079678f0dcf4d13f7a03d73e901e9))
* regenerate the README screenshots from a scripted demo household ([cad3796](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cad3796dc532e34f4699139360c3d814b683abe8))
* reset the working documents and remove the completed implementation plan ([86e740e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/86e740ede410f00b4b0ea516f46c237c2bce71ab))
* rewrite the requirements as a timeless specification ([86e740e](https://github.com/somali-lab/keep-the-house-clean-planner/commit/86e740ede410f00b4b0ea516f46c237c2bce71ab))


### Maintenance

* **agents:** add shared repository instruction layers and portable skills ([ea8221b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/ea8221b02df111bd908535b220ee9816e0dd4c61))
* **workflows:** add safeguarded context maintainer ([ea8221b](https://github.com/somali-lab/keep-the-house-clean-planner/commit/ea8221b02df111bd908535b220ee9816e0dd4c61))

## [1.6.1](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.6.0...v1.6.1) (2026-09-27)


### Bug fixes

* **web:** keep completions sidebar icon from collapsing ([#35](https://github.com/somali-lab/keep-the-house-clean-planner/issues/35)) ([706039c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/706039c232c6f493209ca9fd3eb5e0db1d9820aa))

## [1.6.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.5.4...v1.6.0) (2026-09-27)


### Features

* add completion record management ([eb148c7](https://github.com/somali-lab/keep-the-house-clean-planner/commit/eb148c71f74fba821614d2b31e4d999cad36af33))
* **stats:** add date-scoped statistics purge ([#31](https://github.com/somali-lab/keep-the-house-clean-planner/issues/31)) ([6b77c3f](https://github.com/somali-lab/keep-the-house-clean-planner/commit/6b77c3f0e74f32dfc2741bbfa3856cf34c8e853c))


### Bug fixes

* **docs:** clarify feature branch naming convention in release process ([eb148c7](https://github.com/somali-lab/keep-the-house-clean-planner/commit/eb148c71f74fba821614d2b31e4d999cad36af33))

## [1.5.4](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.5.3...v1.5.4) (2026-09-21)


### Bug fixes

* **web:** widen week overview cards ([#29](https://github.com/somali-lab/keep-the-house-clean-planner/issues/29)) ([e2fa6a3](https://github.com/somali-lab/keep-the-house-clean-planner/commit/e2fa6a338733fc9cecac13f2854a502e112a5a16))

## [1.5.3](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.5.2...v1.5.3) (2026-09-21)


### Bug fixes

* **web:** allow reassignment from today ([#27](https://github.com/somali-lab/keep-the-house-clean-planner/issues/27)) ([ff31a04](https://github.com/somali-lab/keep-the-house-clean-planner/commit/ff31a0415a9d6da8fdae1100a84efabd80db707f))

## [1.5.2](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.5.1...v1.5.2) (2026-09-20)


### Bug fixes

* add scheduling controls and correct due tracking ([#25](https://github.com/somali-lab/keep-the-house-clean-planner/issues/25)) ([b5aa7fb](https://github.com/somali-lab/keep-the-house-clean-planner/commit/b5aa7fb63c0a2820bcecdc3fd89e172d8f0e441d))

## [1.5.1](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.5.0...v1.5.1) (2026-09-20)


### Bug fixes

* **intervals:** backfill three-times-weekly option ([#23](https://github.com/somali-lab/keep-the-house-clean-planner/issues/23)) ([94e320f](https://github.com/somali-lab/keep-the-house-clean-planner/commit/94e320fd02c88ebee85a1bbf4b9773921afe16fb))

## [1.5.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.4.0...v1.5.0) (2026-09-20)


### Features

* **intervals:** add three-times-weekly option ([#21](https://github.com/somali-lab/keep-the-house-clean-planner/issues/21)) ([bd31923](https://github.com/somali-lab/keep-the-house-clean-planner/commit/bd3192326b20adbc80c30bce43c04b1275c433f9))

## [1.4.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.3.0...v1.4.0) (2026-09-20)


### Features

* **settings:** reset execution data from data tab ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))
* **tasks:** default personal task view to one cycle week ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))
* **tasks:** distinguish completing on behalf from taking over ([515e463](https://github.com/somali-lab/keep-the-house-clean-planner/commit/515e46342d08e14811f75bbcbb59b9c43c066de2))
* **today:** browse upcoming days and filter by person ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))


### Bug fixes

* **navigation:** link sidebar logo to week overview with pointer feedback ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))
* **pwa:** clarify offline state and streamline updates ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))
* **today:** hide overdue tasks before cycle start ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))
* **week:** hide tasks before the plan cycle starts ([515e463](https://github.com/somali-lab/keep-the-house-clean-planner/commit/515e46342d08e14811f75bbcbb59b9c43c066de2))


### Build system and dependencies

* **version:** identify local builds with timestamps ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))


### Documentation

* **workflow:** require updated main before feature branches ([67401b0](https://github.com/somali-lab/keep-the-house-clean-planner/commit/67401b051adacb87206e35f40d4b6883f59a50ec))

## [1.3.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.2.0...v1.3.0) (2026-09-18)


### Features

* **ai:** place plan and task assistants in their related workflows ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **navigation:** make week overview the root route and namespace management ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **planner:** simplify planning controls and remove redundant drag targets ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **settings:** organize settings and AI prompts in tabs ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **stats:** add recent-week and cycle period filters ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **stats:** organize every report in compact tabs ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **tasks:** add configurable task completion controls ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **tasks:** collapse rooms and add expand-all controls ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))


### Bug fixes

* **history:** describe task actions with task room and date context ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **logging:** silence successful health checks ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **pdf:** group planner tasks by person and widen the task area ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **stats:** show useful empty states and working completion grouping ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **week:** filter by the selected person and compact overview controls ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))


### Code refactoring

* **container:** slim runtime and isolate backups ([#16](https://github.com/somali-lab/keep-the-house-clean-planner/issues/16)) ([4adbb6c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/4adbb6c06efb8a3b80154fb91c1d4164af8b328b))


### Continuous integration

* run workspace tests in parallel ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))


### Documentation

* **release:** require verified overrides for multi-change pull requests ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))
* **workflow:** require feature branches and coherent commits ([fe0582c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/fe0582ca8f06481e9afd9a64fca12fdfc30aca23))


### Maintenance

* **agent:** add repository guidance and skills ([#14](https://github.com/somali-lab/keep-the-house-clean-planner/issues/14)) ([2507b74](https://github.com/somali-lab/keep-the-house-clean-planner/commit/2507b743fba41b8e201d76b2d2f03cf682fa052d))

## [1.2.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.1.0...v1.2.0) (2026-09-17)


### Features

* **export:** show assignees and use full A4 schedule ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **planner:** filter scheduled tasks by person ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **roles:** add household permissions without passwords ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **stats:** detect planning and completion deviations ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **tasks:** add personal task overview with unassigned work ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **tasks:** preserve room history when moving tasks ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **version:** display app version in the UI and update changelog ([#10](https://github.com/somali-lab/keep-the-house-clean-planner/issues/10)) ([4435ca4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/4435ca41ca2650d5bb82dd6847794bda30dbff70))


### Bug fixes

* **planner:** keep drag targets stable while planning ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **planner:** sync active plan changes to task overviews ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **version:** keep app version visible in all layouts ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **week:** show the original date of moved tasks ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))


### Code refactoring

* **planner:** reveal weekday drop targets during planning ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))


### Tests

* **e2e:** align AI and PDF flows with current controls ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **roles:** cover role fields in audit regressions ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))
* **version:** follow release version dynamically ([#12](https://github.com/somali-lab/keep-the-house-clean-planner/issues/12)) ([b28afb7](https://github.com/somali-lab/keep-the-house-clean-planner/commit/b28afb7b1e640a41c3634641ccca247c949a227e))


### Documentation

* **release:** preserve every changelog entry on squash merge ([726f3c4](https://github.com/somali-lab/keep-the-house-clean-planner/commit/726f3c4a0e152e20976502f047760ca1fcf32b52))

## [1.1.0](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.0.1...v1.1.0) (2026-09-16)


### Features

* **mobile-tasks:** implement mobile task overview page with filtering and display logic ([#8](https://github.com/somali-lab/keep-the-house-clean-planner/issues/8)) ([cc8e80c](https://github.com/somali-lab/keep-the-house-clean-planner/commit/cc8e80c177e65127476059e7c00c0de223cdcc04))


### Continuous integration

* **deps:** upgrade release-please-action to v5 ([#7](https://github.com/somali-lab/keep-the-house-clean-planner/issues/7)) ([c0cf3be](https://github.com/somali-lab/keep-the-house-clean-planner/commit/c0cf3be0ff01181ceccb2cd66f6b48c8c3e5b845))

## [1.0.1](https://github.com/somali-lab/keep-the-house-clean-planner/compare/v1.0.0...v1.0.1) (2026-09-16)


### Code refactoring

* **routes:** update routes for mobile view and legacy redirects ([#4](https://github.com/somali-lab/keep-the-house-clean-planner/issues/4)) ([2f34fa5](https://github.com/somali-lab/keep-the-house-clean-planner/commit/2f34fa535294abbaaa1f8cc2b6d2ef8c84f899e5))


### Maintenance

* **config:** add refactor section to release-please config and create VSCode settings ([#5](https://github.com/somali-lab/keep-the-house-clean-planner/issues/5)) ([85fffc9](https://github.com/somali-lab/keep-the-house-clean-planner/commit/85fffc98f85ad9240f4913d487992b835dead4e8))


### Continuous integration

* avoid duplicate release verification ([a461dab](https://github.com/somali-lab/keep-the-house-clean-planner/commit/a461dabd900b8d7100636e148e1e22d5e702e6f5))

## 1.0.0 (2026-09-16)


### Features

* **ci:** automate releases and GHCR publishing ([14ce653](https://github.com/somali-lab/keep-the-house-clean-planner/commit/14ce65305f89dceb2ff2875e024c80fbc4b5529b))


### Build system and dependencies

* **deps:** bump release-please-action ([#1](https://github.com/somali-lab/keep-the-house-clean-planner/issues/1)) ([7d5a790](https://github.com/somali-lab/keep-the-house-clean-planner/commit/7d5a790c425a370884d28471a7632aee27ff991e))


### Initial release

* add the household planner application ([e90e522](https://github.com/somali-lab/keep-the-house-clean-planner/commit/e90e522c58631f2d1463ce970f8427a097c0fb71))
* add MIT license and attribution guidance ([89569aa](https://github.com/somali-lab/keep-the-house-clean-planner/commit/89569aaab081ff69bc878a53e06f2db0db308f85))

## Changelog

All notable changes to this project are recorded here by the automated release process.
