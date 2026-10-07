import schemas from '../../../shared/shift-report-schemas.json'

// Published versions are immutable, including ordering. Add a new version for
// any change to a question. Legacy reports/audit without a version are v1.
export function schemaFor(version = 1) {
  const schema = schemas[String(version)]
  if (!schema) throw new Error('Nieobsługiwana wersja formularza. Odśwież aplikację.')
  return schema
}
