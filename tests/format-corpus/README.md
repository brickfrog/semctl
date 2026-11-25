# The formatter's regression corpus

Each folder is one case for `sm format` (src/Semctl.Core/Format.fs), run by
tests/Semctl.Tests/FormatCorpusTests.fs:

- `input.dax`: the DAX given to the formatter;
- `expected.dax`: what it should write, taken from the cited source or derived from it by hand
  (never copied from what sm writes);
- `case.json`:
  - `source`: where the case comes from: a public bug report about a DAX formatter (Tabular Editor,
    DAX Studio, DAX Formatter) or a rule in SQLBI's
    [Rules for DAX Code Formatting](https://www.sqlbi.com/articles/rules-for-dax-code-formatting/);
  - `options`: `long`, `width`, `semicolons`, `noSpaceAfterFunction` (the `format` options);
  - `expect`: `same` (sm writes `expected.dax`, and `expected.dax` is a fixed point) or `differs`
    (a known difference: sm deliberately writes something else, and `note` says why). A `differs`
    case that starts to match fails the suite, so the case gets turned into `same`.

Every case also checks that formatting sm's own output changes nothing. Files are compared with
LF line endings.

To add a case: make the folder, write `expected.dax` from the source (not from sm), and cite it.
If sm disagrees and the source is right, fix the formatter; if sm is right by its documented
style (README, "Formatting DAX"), record the case as `differs` with a note.
