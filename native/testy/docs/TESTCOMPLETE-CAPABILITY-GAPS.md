# Dynamic application support: current boundary

Testy discovers the selected process's current windows and controls at runtime. Test authoring is driven by that observed context; neither the inspector nor the AI loop is hardcoded to the supplied sample applications. The samples provide controlled qualification evidence, not a list of the only attachable executables.

This architecture does not establish broad compatibility by itself. Attaching to a process, observing a control, operating it, and independently verifying its business result are different levels of support.

One attachment targets one process identity. Multiple windows and popups from that process are supported within the documented capture limits; a separate helper process or a workflow spanning several applications is not automatically included. Such targets need explicit attachment and appropriate test orchestration.

| Capability | Current Testy implementation | Qualification still required |
|---|---|---|
| Runtime object discovery | Process-scoped UI Automation and opt-in WPF visual-tree inspection | Target providers, framework versions, integrity levels and inaccessible controls |
| Object identification | IDs, names, paths, ancestor-scoped queries and explicitly approved alternatives | A shared reusable alias repository comparable to a full Name Mapping system is not implemented |
| WPF access | Public supported properties, logical items, virtualized controls and supported transactional grid editors | Extensive arbitrary native member/method access and automatic attachment of the probe to uninstrumented binaries are not implemented |
| Third-party controls | Controls exposing supported accessibility patterns can work through those patterns | There is no qualified DevExpress/Telerik/Syncfusion-specific adapter catalog |
| Computer use | Model-directed native/local tools, target-only screenshots, guarded input and engine assertions | Protected/GPU content, unusual popup automation peers and unavailable input acknowledgements remain explicit limits |
| Application families | Windows UIA attachment can discover controls supplied by several frameworks | No equivalent broad framework certification for Java, Delphi, Qt, browser DOMs or mobile applications |
| Execution operations | Durable local same-user queue, schedules, separate preparation/UI evidence and owned-process cleanup | A remote enterprise worker fleet, team permissions and corporate identity integration are not implemented |

SmartBear documents a much broader framework/control catalog, including many third-party controls and dedicated object operations. Its WPF integration exposes native properties/methods with documented exceptions, and its object-mapping facilities associate custom classes with supported control types. Those are substantive engine capabilities; AI does not automatically supply them. [Supported controls](https://support.smartbear.com/testcomplete/docs/general-info/supported-technologies/controls/index.html), [WPF testing](https://support.smartbear.com/testcomplete/docs/app-testing/desktop/wpf/about.html), [Object mapping](https://support.smartbear.com/TestComplete/docs/testing-with/object-identification/mapping.html).

The current product is an AI-directed Windows testing application with dynamic discovery and a qualified WPF subset. It is not yet a universal TestComplete replacement. Broader support requires concrete adapters and repeatable qualification against those actual control libraries, not more optimistic prompts or weaker pass criteria.

The requested customer pilot is excluded from this delivery. Consequently, owned fixtures and available endpoints establish only the measured results reported in the release assessment.
