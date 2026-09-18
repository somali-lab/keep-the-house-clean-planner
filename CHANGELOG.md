# Changelog

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
