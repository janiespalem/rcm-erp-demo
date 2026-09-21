<script setup>
import { computed, inject, nextTick, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '@/composables/useApi'
import { useAuth } from '@/composables/useAuth'
import { schemaFor } from '@/utils/shiftReportSchema'

const { currentUser } = useAuth()
const report = ref(null), form = ref(null), rows = ref([]), todayReports = ref([])
const today = ref(''), entryDate = ref(''), dateFrom = ref(''), dateTo = ref(''), filterShift = ref(''), selectedShift = ref(currentUser.value.default_shift || 'I')
const saved = ref(''), saveState = ref(''), error = ref(''), errors = ref({}), busy = ref(false)
const correction = ref(false), correctionReason = ref(''), latest = ref(null), audit = ref([]), showAudit = ref(false), printPreview = ref(false)
const expired = ref(false), pin = ref(''), offset = ref(0)
const deletedOnly = ref(false), confirmingDelete = ref(false), deleteReason = ref('')
const showDraftSaves = ref(false)
let timer, pendingSave
const registerLogoutGuard = inject('registerLogoutGuard', null)
let unregisterLogoutGuard
const quantities = [['assembled', 'Form złożonych'], ['prepared', 'Form przygotowanych do zalania'], ['poured', 'Gwiazdobloków zalanych na zmianie'], ['checked', 'Form sprawdzonych po ok. 20 minutach'], ['demoulded', 'Prefabrykatów wstępnie rozebranych'], ['damaged', 'Uszkodzone — szt.']]
const checks = computed(() => schemaFor(report.value?.schema_version ?? 1).map(q => q.label))
const equipment = [['vibrators', 'Wibratory'], ['extensions', 'Przedłużacze']]
const notes = [['corrected_work', 'Co poprawiono'], ['remaining_work', 'Co zostało do wykonania'], ['work_owner', 'Osoba odpowiedzialna'], ['remarks', 'Uwagi ogólne']]
const labels = Object.fromEntries([...quantities, ...notes, ['leader', 'Kierownik zmiany'], ['responsible', 'Odpowiedzialny'], ['people', 'Ilość osób'], ['reference', 'Zlecenie / partia'], ['damage_reason', 'Powód uszkodzenia'], ['production_person', 'Osoba odpowiedzialna za produkcję'], ['controller', 'Kierownik / osoba kontrolująca']])
const canWrite = computed(() => ['produkcja', 'technolog'].includes(currentUser.value?.role))
const isAdmin = ref(false)
const owns = computed(() => canWrite.value && report.value && !report.value.deleted_at && (currentUser.value?.role === 'technolog' || report.value.author_id === currentUser.value.id))
const editable = computed(() => owns.value && (report.value.status === 'draft' || correction.value))
const dirty = computed(() => form.value && JSON.stringify(form.value) !== saved.value)
const summary = computed(() => {
  const poured = todayReports.value.reduce((n, r) => n + (r.fields.poured || 0), 0)
  const damaged = todayReports.value.reduce((n, r) => n + (r.fields.damaged || 0), 0)
  return { poured, damaged }
})
const statusName = s => ({ draft: 'Szkic', finalized: 'Zakończony', corrected: 'Skorygowany' }[s] || s)
const time = t => t ? new Date(t.endsWith('Z') || /[+-]\d\d:\d\d$/.test(t) ? t : t + 'Z').toLocaleString('pl-PL') : '—'
const clone = value => JSON.parse(JSON.stringify(value))
const request = (path = '', opts = {}) => api('/shift-reports' + path, { ...opts, quiet: true, preserveAuth: true })

function failure(e) {
  expired.value = e.status === 401
  errors.value = e.detail?.fields || {}
  error.value = expired.value ? 'Sesja wygasła. Wpisz swój PIN poniżej, aby kontynuować bez utraty danych.'
    : e.detail?.message || (typeof e.detail === 'string' ? e.detail : 'Nie udało się zapisać lub pobrać danych. Sprawdź pola i połączenie.')
  if (Array.isArray(e.detail)) {
    for (const item of e.detail) errors.value[item.loc.slice(item.loc[1] === 'fields' ? 2 : 1).join('.')] = 'Sprawdź wartość: ilość musi być nieujemną liczbą całkowitą; tekst nie może być zbyt długi.'
  }
  saveState.value = 'Błąd zapisu — spróbuj ponownie'
  if (Object.keys(errors.value).length) nextTick(() => document.querySelector('.report-editor .field-error:not(:empty)')?.scrollIntoView({ block: 'center', behavior: 'smooth' }))
}
async function refresh() {
  const [history, current] = await Promise.all([
    request(`?${new URLSearchParams({ ...(dateFrom.value && { date_from: dateFrom.value }), ...(dateTo.value && { date_to: dateTo.value }), ...(filterShift.value && { shift: filterShift.value }), offset: offset.value, deleted: deletedOnly.value })}`),
    request('/today'),
  ])
  rows.value = history; todayReports.value = current.reports; today.value = current.date
  isAdmin.value = current.can_admin_delete === true
  if (!entryDate.value) entryDate.value = current.date
}
async function loadHistory(reset = true) {
  if (reset) offset.value = 0
  try { await refresh() } catch (e) { failure(e) }
}
function install(data) {
  schemaFor(data.schema_version ?? 1)
  clearTimeout(timer)
  report.value = data; form.value = clone(data.fields); saved.value = JSON.stringify(form.value)
  correction.value = false; correctionReason.value = ''; latest.value = null; error.value = ''; errors.value = {}
  confirmingDelete.value = false; deleteReason.value = ''
  audit.value = []; showAudit.value = false; printPreview.value = false; saveState.value = `Zapisano ${time(data.updated_at)}`
}
async function leaveForm() {
  if (pendingSave) await pendingSave
  if (dirty.value || correction.value) return window.confirm('Masz niezapisane dane lub otwartą korektę. Odrzucić je i otworzyć inny raport?')
  return true
}
async function open(id) {
  if (!(await leaveForm())) return
  busy.value = true
  try { install(await request(`/${id}`)) } catch (e) { failure(e) } finally { busy.value = false }
}
async function openToday(useDate = false) {
  if (!(await leaveForm())) return
  busy.value = true
  try {
    const current = await request('/today'); today.value = current.date
    const day = useDate === true ? entryDate.value : current.date
    if (!day) { error.value = 'Wybierz datę raportu.'; return }
    const candidates = day === current.date ? current.reports : await request(`?date_from=${day}&date_to=${day}`)
    const existing = candidates.find(r => r.shift === selectedShift.value)
    if (existing) install(existing)
    else if (canWrite.value) install(await request('', { method: 'POST', body: { report_date: day, shift: selectedShift.value } }))
    else error.value = 'Nie ma jeszcze raportu dla tej zmiany.'
    await refresh()
  } catch (e) { failure(e) } finally { busy.value = false }
}
async function save() {
  clearTimeout(timer)
  if (pendingSave) { await pendingSave; if (error.value) return false }
  if (!dirty.value || !editable.value || correction.value) return !dirty.value
  const fields = clone(form.value), id = report.value.id
  saveState.value = 'Zapisywanie…'; error.value = ''; errors.value = {}
  pendingSave = (async () => {
    try {
      const data = await request(`/${id}`, { method: 'PUT', body: { version_id: report.value.version_id, fields } })
      report.value = data; saved.value = JSON.stringify(fields)
      saveState.value = `Zapisano ${time(data.updated_at)}`
      return true
    } catch (e) { failure(e); return false }
  })()
  const ok = await pendingSave; pendingSave = null
  if (ok && dirty.value) return save()
  return ok
}
watch(form, () => {
  clearTimeout(timer)
  if (dirty.value && editable.value && !correction.value) {
    saveState.value = 'Niezapisane zmiany'
    timer = setTimeout(save, 700)
  }
}, { deep: true })
async function finish() {
  busy.value = true
  try {
    if (!correction.value && !(await save())) return
    const path = correction.value ? 'corrections' : 'finalize'
    const body = { version_id: report.value.version_id, ...(correction.value && { fields: clone(form.value), reason: correctionReason.value }) }
    const data = await request(`/${report.value.id}/${path}`, { method: 'POST', body })
    install(data); await refresh()
  } catch (e) { failure(e) } finally { busy.value = false }
}
async function removeDraft() {
  if (!window.confirm('Usunąć ten niedokończony szkic? Tej operacji nie można cofnąć.')) return
  busy.value = true; clearTimeout(timer)
  try {
    if (pendingSave) await pendingSave
    await request(`/${report.value.id}?version_id=${report.value.version_id}`, { method: 'DELETE' })
    report.value = null; form.value = null; saved.value = ''; error.value = ''; await refresh()
  } catch (e) { failure(e) } finally { busy.value = false }
}
async function removeAsAdmin() {
  if (!deleteReason.value.trim()) { error.value = 'Podaj powód usunięcia raportu.'; return }
  busy.value = true; clearTimeout(timer)
  try {
    if (pendingSave) await pendingSave
    await request(`/${report.value.id}/delete`, { method: 'POST', body: { version_id: report.value.version_id, reason: deleteReason.value.trim() } })
    report.value = null; form.value = null; saved.value = ''; error.value = ''; correction.value = false
    confirmingDelete.value = false; deleteReason.value = ''; latest.value = null
    await refresh()
  } catch (e) { failure(e) } finally { busy.value = false }
}
async function compare() {
  try { latest.value = await request(`/${report.value.id}`) } catch (e) { failure(e) }
}
function useLatestVersion() {
  report.value = latest.value; saved.value = JSON.stringify(latest.value.fields)
  if (report.value.status !== 'draft') correction.value = true
  latest.value = null; error.value = ''; saveState.value = 'Sprawdzono aktualną wersję. Zapisz swoje dane przyciskiem.'
}
async function loadAudit(toggle = true) {
  try { audit.value = await request(`/${report.value.id}/audit?include_drafts=${showDraftSaves.value}`); showAudit.value = toggle ? !showAudit.value : true } catch (e) { failure(e) }
}
function changes(before, after, schemaVersion = 1) {
  const result = []
  for (const key of Object.keys(after || {})) {
    if (JSON.stringify(before?.[key]) === JSON.stringify(after[key])) continue
    if (key === 'checks') schemaFor(schemaVersion).forEach(({ label }, i) => { if (before?.checks?.[i] !== after.checks[i]) result.push({ label: `${i + 1}. ${label}`, before: before?.checks?.[i] ?? '—', after: after.checks[i] ?? '—' }) })
    else if (['vibrators', 'extensions'].includes(key)) {
      for (const [field, label] of [['condition', 'Stan'], ['reason', 'Powód / osoba'], ['note', 'Uwaga / nr']]) {
        if (before?.[key]?.[field] !== after[key][field]) result.push({ label: equipment.find(e => e[0] === key)[1] + ': ' + label, before: before?.[key]?.[field] || '—', after: after[key][field] || '—' })
      }
    } else result.push({ label: labels[key] || key, before: before?.[key] ?? '—', after: after[key] ?? '—' })
  }
  return result
}
async function renew() {
  try {
    const response = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ role: currentUser.value.role, pin: pin.value }) })
    pin.value = ''
    if (!response.ok) throw new Error()
    const data = await response.json()
    if (data.id !== currentUser.value.id) { error.value = 'Użyj PIN-u osoby, która rozpoczęła tę sesję.'; return }
    currentUser.value.token = data.access_token; expired.value = false; error.value = ''; saveState.value = 'Sesja odnowiona. Możesz zapisać dane.'
  } catch { error.value = 'Nie udało się odnowić sesji. Sprawdź PIN i połączenie.' }
}
function beforeUnload(event) { if (dirty.value || correction.value) { event.preventDefault(); event.returnValue = '' } }
async function prepareLogout() {
  if (busy.value) return false
  busy.value = true
  try {
    if (pendingSave) await pendingSave
    if (!dirty.value && !correction.value) return true
    if (!correction.value && await save()) return true
    return window.confirm('Raport zawiera niezapisane dane lub niezatwierdzoną korektę. Wylogować i odrzucić te zmiany? Anuluj, aby wrócić do formularza.')
  } finally {
    busy.value = false
  }
}
function printReport() { window.print() }
onMounted(() => { loadHistory(); unregisterLogoutGuard = registerLogoutGuard?.(prepareLogout); window.addEventListener('beforeunload', beforeUnload) })
onBeforeUnmount(() => { unregisterLogoutGuard?.(); clearTimeout(timer); window.removeEventListener('beforeunload', beforeUnload) })
</script>

<template>
  <section class="shift-module">
    <div class="shift-top"><div><h1>Raporty zmianowe</h1><p>Produkcja Gwiazdobloków · {{ today }}</p></div>
      <div class="shift-actions"><label>Data raportu<input v-model="entryDate" type="date"></label><label>Zmiana <select v-model="selectedShift" aria-label="Zmiana dzisiejszego raportu"><option>I</option><option>II</option></select></label><button class="btn btn-primary" :disabled="busy" @click="openToday()">Dzisiejszy raport</button><button class="btn btn-outline" :disabled="busy" @click="openToday(true)">Otwórz wybraną datę</button></div>
    </div>
    <div v-if="error" class="shift-error" role="alert">{{ error }} <span>Wpisane dane pozostają w formularzu.</span>
      <button v-if="report && !expired" class="btn btn-outline" @click="compare">Porównaj z aktualną wersją</button>
    </div>
    <div v-if="expired" class="shift-actions"><label>Twój PIN <input v-model="pin" type="password" inputmode="numeric" maxlength="4" @keyup.enter="renew"></label><button class="btn btn-primary" @click="renew">Odnów sesję</button></div>
    <div v-if="latest" class="shift-panel">
      <h2>Porównanie z serwerem · wersja {{ latest.version_id }} · {{ statusName(latest.status) }}</h2>
      <table><thead><tr><th>Pole</th><th>Na serwerze</th><th>Twój formularz</th></tr></thead><tbody><tr v-for="c in changes(latest.fields, form)" :key="c.label"><td>{{ c.label }}</td><td>{{ c.before }}</td><td>{{ c.after }}</td></tr></tbody></table>
      <button class="btn btn-outline" @click="useLatestVersion">Porównano — zachowaj moje wartości do zapisu</button>
    </div>

    <div v-if="report" class="shift-panel report-editor">
      <div class="shift-top"><div><h2>Raport {{ report.report_date }} · Zmiana {{ report.shift }}</h2><p>Autor: {{ report.author_name }} · <strong>{{ statusName(report.status) }}</strong> · wersja {{ report.version_id }}</p></div></div>
      <div role="status" class="save-state">{{ correction ? 'Korekta — zapisz po uzupełnieniu' : saveState }}</div>
      <p v-if="report.deleted_at" class="shift-warning">Usunięty {{ time(report.deleted_at) }}. Raport nie jest uwzględniany w historii produkcji ani podsumowaniach. Powód i administrator są zapisani w historii zmian.</p>
      <p v-if="!editable">Raport tylko do odczytu. {{ report.finalized_at ? 'Zakończono: ' + time(report.finalized_at) + ' · ' + report.finalized_by_name : 'Edycja jest dostępna dla autora i technologa.' }}</p>
      <div v-if="correction" class="correction-box"><label>Powód korekty <textarea v-model="correctionReason" maxlength="2000" /></label><small class="field-error">{{ errors.correction_reason }}</small><p>Korekta zapisze się po zatwierdzeniu. Obecny zakończony raport pozostaje dostępny w historii.</p></div>
      <fieldset :disabled="!editable || busy || confirmingDelete">
        <div class="shift-grid header-grid">
          <label v-for="key in ['leader', 'responsible', 'people', 'reference']" :key="key">{{ labels[key] }}<input v-if="key === 'people'" :value="form[key]" type="number" inputmode="numeric" min="1" step="1" @input="form[key] = $event.target.value === '' ? null : $event.target.valueAsNumber"><input v-else v-model="form[key]" :maxlength="key === 'reference' ? 200 : 100"><small class="field-error">{{ errors[key] }}</small></label>
        </div>
        <h3>1. Ilości</h3>
        <div class="shift-grid quantity-grid"><label v-for="[key, label] in quantities" :key="key">{{ label }}<input :value="form[key]" type="number" inputmode="numeric" min="0" max="1000000" step="1" @input="form[key] = $event.target.value === '' ? null : $event.target.valueAsNumber"><small class="field-error">{{ errors[key] }}</small></label></div>
        <label>Powód uszkodzenia <span v-if="form.damaged > 0">(wymagany)</span><input v-model="form.damage_reason" maxlength="2000"><small class="field-error">{{ errors.damage_reason }}</small></label>
        <h3>2. Sprzęt na produkcji</h3>
        <div v-for="[key, label] in equipment" :key="key" class="equipment-row">
          <label>{{ label }}<select v-model="form[key].condition" :aria-label="label"><option :value="null">Wybierz stan</option><option value="sprawne">Sprawne</option><option value="niesprawne">Niesprawne</option></select><small class="field-error">{{ errors[key + '.condition'] }}</small></label>
          <label>Powód / osoba odpowiedzialna<input v-model="form[key].reason" maxlength="1000"><small class="field-error">{{ errors[key + '.reason'] }}</small></label>
          <label>Uwaga / nr<input v-model="form[key].note" maxlength="1000"></label>
        </div>
        <h3>3. Kontrola końcowa</h3>
        <div v-for="(label, i) in checks" :key="i" class="check-row">
          <div :id="'check-label-' + i">{{ i + 1 }}. {{ label }}<small class="field-error">{{ errors['checks.' + i] }}</small></div>
          <div class="check-options" role="group" :aria-labelledby="'check-label-' + i"><button v-for="value in ['OK', 'NIE', 'N/D']" :key="value" type="button" :aria-pressed="form.checks[i] === value" :class="{ chosen: form.checks[i] === value, failed: value === 'NIE' && form.checks[i] === value }" @click="form.checks[i] = value">{{ value }}</button></div>
        </div>
        <h3>4. Uwagi i niezgodności</h3>
        <div class="shift-grid notes-grid"><label v-for="[key, label] in notes" :key="key">{{ label }}<textarea v-model="form[key]" :maxlength="key === 'work_owner' ? 100 : 4000" rows="2" /><small class="field-error">{{ errors[key] }}</small></label></div>
        <div class="shift-grid notes-grid closing"><label v-for="key in ['production_person', 'controller']" :key="key">{{ labels[key] }}<input v-model="form[key]" maxlength="100"><small class="field-error">{{ errors[key] }}</small></label></div>
      </fieldset>
      <div class="shift-actions report-actions">
        <button v-if="editable && !correction" class="btn btn-outline" :disabled="busy" @click="save">Zapisz teraz / spróbuj ponownie</button>
        <button v-if="editable" class="btn btn-primary" :disabled="busy" @click="finish">{{ correction ? 'Zatwierdź korektę' : 'Zakończ raport' }}</button>
        <button v-if="owns && report.status !== 'draft' && !correction" class="btn btn-outline" @click="correction = true">Koryguj raport</button>
        <button v-if="owns && report.status === 'draft' && !isAdmin" class="btn btn-outline" :disabled="busy" @click="removeDraft">Usuń niedokończony szkic</button>
        <button v-if="isAdmin && !report.deleted_at" class="btn btn-outline delete-report" :disabled="busy" @click="confirmingDelete = true">Usuń raport (administrator)</button>
        <button class="btn btn-outline" @click="loadAudit">Historia zmian</button>
        <button class="btn btn-outline" @click="printPreview = !printPreview">Podgląd A4</button>
        <button class="btn btn-outline" @click="printReport">Drukuj / Zapisz PDF</button>
      </div>
      <form v-if="confirmingDelete" class="correction-box delete-confirmation" @submit.prevent="removeAsAdmin">
        <h3>Potwierdzenie usunięcia</h3>
        <p>Usuwasz raport {{ report.report_date }}, zmiana {{ report.shift }}, autor: {{ report.author_name }}.</p>
        <p>Zniknie z bieżącej historii i podsumowań. Będzie można utworzyć nowy raport na tę datę i zmianę. Dane oraz audyt pozostaną w sekcji „Usunięte”. Niezapisane zmiany formularza zostaną odrzucone.</p>
        <label>Powód usunięcia<textarea v-model="deleteReason" required maxlength="2000" placeholder="Np. raport testowy" :disabled="busy" /></label>
        <div class="shift-actions"><button type="submit" class="btn btn-primary delete-report" :disabled="busy || !deleteReason.trim()">Potwierdź usunięcie</button><button type="button" class="btn btn-outline" :disabled="busy" @click="confirmingDelete = false">Anuluj usunięcie</button></div>
      </form>
      <div v-if="showAudit" class="audit-list"><h3>Historia zmian</h3><label><input v-model="showDraftSaves" type="checkbox" @change="loadAudit(false)"> Pokaż zapisy szkicu</label><details v-for="entry in audit" :key="entry.id"><summary>{{ time(entry.created_at) }} · {{ entry.actor_name }} · {{ {created:'Utworzono', saved:'Zapisano szkic', finalized:'Zakończono', corrected:'Korekta', deleted:'Usunięto przez administratora'}[entry.action] }} · wersja {{ entry.after.version_id }}</summary><p v-if="entry.after.draft_save_count">Zapisów szkicu: {{ entry.after.draft_save_count }} · Ostatni zapis: {{ time(entry.after.last_saved_at) }}. Zmiany poniżej porównują początek i koniec tej serii zapisów, nie każdy etap pisania.</p><p v-if="entry.reason">Powód: {{ entry.reason }}</p><table><thead><tr><th>Pole</th><th>Przed</th><th>Po</th></tr></thead><tbody><tr v-for="c in changes(entry.before?.fields, entry.after.fields, entry.after.schema_version ?? 1)" :key="c.label"><td>{{ c.label }}</td><td>{{ c.before }}</td><td>{{ c.after }}</td></tr></tbody></table></details></div>
    </div>

    <article v-if="report" class="shift-print" :class="{ preview: printPreview }">
      <h2>RAPORT ZMIANY – PRODUKCJA GWIAZDOBLOKÓW</h2>
      <p>Na koniec zmiany wpisz ilości i zaznacz X w kolumnie OK, NIE lub N/D.</p>
      <p><strong>{{ report.deleted_at ? 'USUNIĘTY · ' : '' }}{{ statusName(report.status) }}{{ dirty || correction ? ' · NIEZATWIERDZONE ZMIANY' : '' }}</strong> · Raport #{{ report.id }} · wersja {{ report.version_id }}</p>
      <div class="print-header"><span>Data: {{ report.report_date }}</span><span>Zmiana: {{ report.shift }}</span><span v-for="key in ['leader','responsible','people','reference']" :key="key">{{ labels[key] }}: {{ form[key] ?? '—' }}</span></div>
      <h3>1. ILOŚCI</h3><table><tbody><tr v-for="[key,label] in quantities" :key="key"><td>{{ label }}</td><td>{{ form[key] ?? '—' }}</td></tr><tr><td>Powód uszkodzenia</td><td>{{ form.damage_reason || '—' }}</td></tr></tbody></table>
      <h3>2. SPRZĘT NA PRODUKCJI</h3><table><thead><tr><th>Sprzęt</th><th>Stan</th><th>Powód / osoba odpowiedzialna</th><th>Uwaga / nr</th></tr></thead><tbody><tr v-for="[key,label] in equipment" :key="key"><td>{{ label }}</td><td>{{ form[key].condition || '—' }}</td><td>{{ form[key].reason }}</td><td>{{ form[key].note }}</td></tr></tbody></table>
      <h3>3. KONTROLA KOŃCOWA</h3><table><thead><tr><th>Kontrola</th><th>OK</th><th>NIE</th><th>N/D</th></tr></thead><tbody><tr v-for="(label,i) in checks" :key="i"><td>{{ i+1 }}. {{ label }}</td><td v-for="v in ['OK','NIE','N/D']" :key="v">{{ form.checks[i] === v ? 'X' : '' }}</td></tr></tbody></table>
      <h3>4. UWAGI I NIEZGODNOŚCI</h3><p v-for="[key,label] in notes" :key="key"><strong>{{ label }}:</strong> {{ form[key] || '—' }}</p>
      <div class="print-header"><span>Osoba odpowiedzialna za produkcję: {{ form.production_person }}</span><span>Kierownik / osoba kontrolująca: {{ form.controller }}</span></div>
      <p>Autor: {{ report.author_name }} · Utworzono: {{ time(report.created_at) }}<br>Zakończono: {{ time(report.finalized_at) }} · {{ report.finalized_by_name }}<br>Ostatnia zmiana: {{ time(report.updated_at) }} · Korekty: {{ report.correction_count }}</p>
    </article>

    <div class="shift-panel history-panel"><h2>Historia raportów</h2>
      <div v-if="isAdmin" class="shift-actions history-mode"><button class="btn btn-outline" :aria-pressed="!deletedOnly" @click="deletedOnly = false; loadHistory()">Bieżące</button><button class="btn btn-outline" :aria-pressed="deletedOnly" @click="deletedOnly = true; loadHistory()">Usunięte</button></div>
      <p v-if="deletedOnly">Archiwum usuniętych raportów — tylko dla administratora.</p>
      <div class="daily-summary"><span v-for="s in ['I','II']" :key="s">Dzisiaj {{ s }}: <strong>{{ statusName(todayReports.find(r => r.shift === s)?.status) || 'Brak raportu' }}</strong></span><span>Zalano: <strong>{{ summary.poured }} szt.</strong></span><span>Uszkodzone: <strong>{{ summary.damaged }} szt.</strong></span></div>
      <p class="summary-note">Ilości z dzisiejszych raportów, również szkiców. Uszkodzenia mogą dotyczyć wcześniejszego zalewania — pokazujemy liczby sztuk, bez wskaźnika procentowego.</p>
      <p v-if="todayReports.some(r => r.warning)" class="shift-warning">Uwaga: NIE w kontroli, niesprawny sprzęt lub prace do wykonania.</p>
      <form class="shift-actions history-filters" @submit.prevent="loadHistory()"><label>Od daty<input v-model="dateFrom" type="date"></label><label>Do daty<input v-model="dateTo" type="date"></label><label>Zmiana<select v-model="filterShift"><option value="">Wszystkie</option><option>I</option><option>II</option></select></label><button class="btn btn-outline">Filtruj</button></form>
      <table class="report-history"><thead><tr><th>Data / zmiana</th><th>Autor</th><th>Status</th><th>Zalano / uszkodzone</th><th>Uwagi</th><th></th></tr></thead><tbody><tr v-for="r in rows" :key="r.id"><td data-label="Data / zmiana">{{ r.report_date }} · {{ r.shift }}</td><td data-label="Autor">{{ r.author_name }}</td><td data-label="Status">{{ statusName(r.status) }}</td><td data-label="Zalano / uszkodzone">{{ r.fields.poured ?? '—' }} / {{ r.fields.damaged ?? '—' }}</td><td data-label="Uwagi">{{ r.warning ? '⚠ Wymaga uwagi' : '—' }}</td><td><button class="btn btn-outline" :disabled="busy" @click="open(r.id)">Otwórz</button></td></tr></tbody></table>
      <p v-if="!rows.length">Brak raportów dla wybranych filtrów.</p><div class="shift-actions"><button v-if="offset" class="btn btn-outline" @click="offset = Math.max(0, offset - 100); loadHistory(false)">Poprzednie</button><button v-if="rows.length === 100" class="btn btn-outline" @click="offset += 100; loadHistory(false)">Następne</button></div>
    </div>
  </section>
</template>

<style scoped>
.shift-module { max-width:1200px; margin:0 auto; padding:24px; color:var(--text); }
.shift-top,.shift-actions { display:flex; align-items:center; justify-content:space-between; gap:14px; flex-wrap:wrap; }
h1 { font-size:26px; margin:0 0 5px; } h2 { font-size:19px; margin:0 0 8px; } p { margin:6px 0 12px; }
.shift-panel { background:var(--surface,white); border:1px solid var(--border); border-radius:8px; padding:22px; margin-top:20px; }
fieldset { border:0; padding:0; margin:0; min-width:0; } fieldset:disabled { opacity:1; }
label { display:block; font-size:14px; font-weight:600; }
input,select,textarea { display:block; width:100%; min-height:44px; margin-top:6px; padding:9px 10px; font:inherit; color:inherit; background:var(--surface,white); border:1px solid #99a6af; border-radius:5px; }
input:disabled,select:disabled,textarea:disabled { color:var(--text); opacity:1; background:#f3f5f6; }
input:focus,select:focus,textarea:focus,button:focus-visible { outline:3px solid #d29936; outline-offset:2px; }
.shift-grid { display:grid; gap:16px; } .header-grid { grid-template-columns:repeat(4,1fr); margin-top:18px; }.quantity-grid { grid-template-columns:repeat(3,1fr); margin-bottom:16px; }.quantity-grid input { font-size:24px; font-weight:700; }
h3 { margin:24px 0 14px; padding-bottom:8px; border-bottom:2px solid #364e59; font-size:16px; text-transform:uppercase; letter-spacing:.04em; }
.equipment-row { display:grid; grid-template-columns:1fr 2fr 1fr; gap:16px; margin:14px 0; }
.check-row { display:grid; grid-template-columns:1fr auto; align-items:center; gap:18px; padding:10px 0; border-bottom:1px solid var(--border); font-size:15px; }
.check-options { display:flex; gap:6px; }.check-options button { min-width:65px; min-height:46px; border:1px solid #91a0a8; border-radius:5px; background:white; color:#243d48; font-size:16px; font-weight:700; cursor:pointer; }.check-options .chosen { background:#254b42; border-color:#254b42; color:white; }.check-options .failed { background:#a62a24; border-color:#a62a24; }.check-options button:disabled { cursor:default; }
.notes-grid { grid-template-columns:1fr 1fr; }.closing { margin-top:20px; }.report-actions { justify-content:flex-start; border-top:1px solid var(--border); margin-top:24px; padding-top:18px; }.btn { min-height:44px; }
.save-state { font-size:14px; font-weight:600; }.field-error { color:#ac2721; display:block; font-weight:600; margin-top:3px; }.field-error:empty { display:none; }
.delete-report { color:#a62a24; border-color:#a62a24; }.delete-report.btn-primary { background:#a62a24; color:white; }.delete-confirmation .shift-actions { margin-top:14px; }.history-mode { justify-content:flex-start; }.history-mode [aria-pressed="true"] { background:#254b42; color:white; }
.audit-list input[type="checkbox"] { width:auto; min-height:0; margin-right:8px; }
.shift-error,.shift-warning,.correction-box { padding:14px; margin-top:15px; border:1px solid #c28137; background:#fff5df; color:#65370d; }.shift-error span { display:block; }.shift-error .btn { margin-top:8px; }
table { width:100%; border-collapse:collapse; font-size:14px; }td,th { padding:10px; text-align:left; border-bottom:1px solid var(--border); overflow-wrap:anywhere; }th { font-size:12px; text-transform:uppercase; } .daily-summary { display:flex; flex-wrap:wrap; gap:16px; margin:20px 0 10px; }.summary-note { color:var(--muted); font-size:12px; }.history-filters { justify-content:flex-start; margin:18px 0; align-items:flex-end; }.audit-list details { margin:12px 0; }summary { cursor:pointer; }
.shift-print { display:none; }.shift-print.preview { display:block; max-width:210mm; margin:24px auto; padding:12mm; background:white; color:black; box-shadow:0 2px 14px #0002; font-size:11px; }.print-header { display:grid; grid-template-columns:1fr 1fr; gap:6px; }.shift-print h2 { font-size:15px; }.shift-print h3 { font-size:12px; margin:12px 0 6px; }.shift-print table { font-size:10px; }.shift-print th,.shift-print td { border:1px solid #777; padding:4px; }.shift-print p { white-space:pre-wrap; overflow-wrap:anywhere; }
@media(max-width:760px) { .shift-module { padding:12px; }.shift-panel { padding:14px; }.header-grid,.quantity-grid { grid-template-columns:1fr 1fr; }.equipment-row,.notes-grid { grid-template-columns:1fr; }.check-row { grid-template-columns:1fr; gap:8px; }.history-panel { overflow-x:auto; } }
@media screen and (max-width:600px) {
  .shift-module { padding:0; min-width:0; }
  .shift-top > div,.shift-actions,label { min-width:0; max-width:100%; }
  .shift-top > .shift-actions { display:grid; grid-template-columns:minmax(0,1fr) minmax(0,1fr); width:100%; gap:10px; }
  .shift-top > .shift-actions > button { grid-column:1 / -1; width:100%; }
  .header-grid { grid-template-columns:minmax(0,1fr); }
  .quantity-grid { grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
  .quantity-grid label { display:flex; flex-direction:column; justify-content:space-between; }
  input,select,textarea { min-width:0; max-width:100%; font-size:16px; min-height:48px; }
  .btn { min-height:48px; white-space:normal; justify-content:center; }
  .check-options button { flex:1; min-height:50px; }
  .save-state { position:sticky; top:0; z-index:5; padding:12px 8px; background:#edf4ef; border-bottom:1px solid #b5cabe; line-height:1.4; overflow-wrap:anywhere; }
  .report-actions { display:grid; grid-template-columns:minmax(0,1fr); }
  .report-actions .btn { width:100%; }
  .history-filters { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); align-items:end; gap:10px; }
  .daily-summary { display:grid; grid-template-columns:1fr; gap:10px; }
  .report-history,.report-history tbody { display:block; }
  .report-history thead { display:none; }
  .report-history tr { display:block; border:1px solid var(--border); border-radius:6px; padding:10px; margin:12px 0; }
  .report-history td { display:grid; grid-template-columns:minmax(0,1fr) minmax(0,1fr); gap:8px; padding:8px 0; font-family:inherit; font-size:14px; }
  .report-history td::before { content:attr(data-label); color:var(--muted); font-size:12px; }
  .report-history td:last-child { display:block; border:0; }
  .report-history td:last-child::before { display:none; }
  .report-history .btn { width:100%; }
  .shift-print.preview { padding:12px; overflow-x:auto; }
  .shift-error { overflow-wrap:anywhere; }
}
</style>
<style>
@media print {
  @page { size:A4; margin:12mm; }
  body:has(.shift-print) * { visibility:hidden; }
  body:has(.shift-print) .app-header, body:has(.shift-print) nav, .shift-module > :not(.shift-print) { display:none !important; }
  body:has(.shift-print) main, body:has(.shift-print) .shift-module { margin:0; padding:0; max-width:none; }
  body:has(.shift-print) .shift-print, body:has(.shift-print) .shift-print * { visibility:visible; }
  body:has(.shift-print) .shift-print { display:block !important; position:static; width:100%; max-width:none !important; margin:0 !important; padding:0 !important; box-shadow:none !important; color:black; background:white; font-size:9pt !important; line-height:1.2; }
  body .shift-print table { font-size:9pt; }
  body .shift-print th, body .shift-print td { font-family:inherit; font-size:inherit; line-height:1.2; padding:3px; }
  .shift-print tr,.shift-print .print-header { break-inside:avoid; }
  .shift-print h3 { break-after:avoid; }
}
</style>
