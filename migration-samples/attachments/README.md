# Synthetic attachment layout

The three placeholder files below are committed and match `attachment-manifest.csv`. `tools/generate-migration-sample-attachments.py` regenerates them and `../sample-attachments.zip` (deterministic; `--check` verifies nothing drifted):

- `DEMO-AGR-0001/DEMO-AGR-0001_executed.pdf`
- `DEMO-AGR-0001/DEMO-AGR-0001_schedule.docx`
- `DEMO-AGR-0002/DEMO-AGR-0002_executed.pdf`

`../sample-attachments.zip` holds `attachment-manifest.csv` plus these files and is what the Data migration demonstration uploads. Use synthetic placeholders only.
