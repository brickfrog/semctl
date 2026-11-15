# VertiPaq Analyzer test file

`Contoso.vpax` is a VertiPaq Analyzer file made by Bravo 1.0.2 (Dax.Model.Extractor 1.2.11) from SQLBI's Contoso
sample model. It is copied unchanged from SQLBI's VertiPaq-Analyzer repository
(`tests/Dax.Vpax.Tests/_data/Contoso.vpax`, https://github.com/sql-bi/VertiPaq-Analyzer), which is released under
the MIT License, Copyright (c) 2019 SQLBI. The `vertipaq` tests read it as a file tess didn't write: its sizes and
counts are checked against the file's own DaxVpaView.json, and one relationship has 12,535 rows that break
referential integrity, which the Best Practice rule `FIX_REFERENTIAL_INTEGRITY_VIOLATIONS` finds.
