<script setup>
import { ref, computed } from 'vue'
import { api } from '@/composables/useApi'
import { useOrders } from '@/composables/useOrders'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { defaultDeadline } from '@/utils/format'

const emit = defineEmits(['switch-tab'])

const { loadOrders, uniqueClients, focusOrderId } = useOrders()
const { approvedMaterials } = useApprovedMaterials()

const CATEGORY_OPTIONS = [
  { key: 'remont', title: 'Remont' },
  { key: 'nowa_czesc', title: 'Nowa część' },
  { key: 'catalog', title: 'Katalog' },
  { key: 'zbrojenie', title: 'Projekt zastrzeżony' },
]

// Własne firmy — zlecenie wewnętrzne (koszt własny zamiast oferty z marżą/VAT)
const INTERNAL_FIRMS = ['DemoFab', 'Demo Partner']

const EMPTY_FORM = () => ({
  client: '',
  deadline: defaultDeadline(),
  description: '',
  quantity: 1,
  material: '',
  materials_json: [{ name: '', qty_kg: '' }],
  // advanced / optional
  order_number: '',
  notes: '',
  estimated_value: 0,
  has_drawing: false,
  requires_visit: false,
  is_defence: false,
  is_internal: false,
  // execution & delivery details
  weight_kg: '',
  drawing_number: '',
  dimensions: '',
  delivery_address: '',
  contact: '',
  // sane backend defaults — not exposed as user choices
  order_type: 'remont',
  sop_name: null,
  template_id: null,
  approved_material_id: null,
  purpose: null,
})

const form        = ref(EMPTY_FORM())
const showMore     = ref(false)
const saving       = ref(false)
const error        = ref(null)
const result       = ref(null)   // triage result → success screen

const materialNames = computed(() =>
  [...approvedMaterials.value]
    .map(m => m.name)
    .sort((a, b) => a.localeCompare(b, 'pl', { numeric: true }))
)

const canSubmit = computed(() =>
  !!form.value.client.trim() &&
  !!form.value.deadline &&
  !!form.value.description.trim() &&
  Number(form.value.quantity) >= 1
)

const RESULT_INFO = {
  standard: {
    title: 'Zlecenie utworzone — pasuje do szablonu z katalogu',
    sub:   'Technolog potwierdzi wycenę i dostaniesz ją do zatwierdzenia.',
  },
  niestandard: {
    title: 'Zlecenie utworzone — czeka na wycenę',
    sub:   'Trafiło do Technologa. Dostaniesz wycenę do zatwierdzenia.',
  },
  odrzut: {
    title: 'Zlecenie odrzucone przez kontrolę',
    sub:   'Sprawdź dane (materiał, termin) i utwórz ponownie, jeśli to pomyłka.',
  },
}

function resetForm() {
  form.value  = EMPTY_FORM()
  showMore.value = false
  error.value = null
  result.value = null
}

function selectCategory(key) {
  form.value.order_type = key
  form.value.is_defence = key === 'zbrojenie'
}

function selectFirm(firm) {
  // Toggle: drugie kliknięcie tej samej firmy → wraca do klienta zewnętrznego
  if (form.value.is_internal && form.value.client === firm) {
    form.value.is_internal = false
    form.value.client = ''
  } else {
    form.value.client = firm
    form.value.is_internal = true
  }
}

function addMaterialLine() {
  form.value.materials_json.push({ name: '', qty_kg: '' })
}

function removeMaterialLine(index) {
  form.value.materials_json.splice(index, 1)
  if (!form.value.materials_json.length) addMaterialLine()
}

function cleanMaterials() {
  return form.value.materials_json
    .map(line => ({
      name: String(line.name || '').trim(),
      qty_kg: line.qty_kg !== '' && line.qty_kg !== null ? Number(line.qty_kg) : 0,
    }))
    .filter(line => line.name)
}

// Ręczna edycja pola Klient = klient zewnętrzny (chyba że wpisano dokładnie nazwę własnej firmy)
function onClientInput() {
  form.value.is_internal = INTERNAL_FIRMS.includes(form.value.client.trim())
}

async function submit() {
  if (!canSubmit.value || saving.value) return
  error.value = null
  saving.value = true
  try {
    const f = form.value
    const payload = { ...f }
    const materials = cleanMaterials()
    payload.materials_json = materials
    payload.material = materials[0]?.name || ''
    // weight_kg: send Number if filled, omit if empty
    if (f.weight_kg !== '' && f.weight_kg !== null) {
      payload.weight_kg = Number(f.weight_kg)
    } else {
      delete payload.weight_kg
    }
    // string delivery fields: omit if empty
    if (!f.drawing_number) delete payload.drawing_number
    if (!f.dimensions) delete payload.dimensions
    if (!f.delivery_address) delete payload.delivery_address
    if (!f.contact) delete payload.contact
    const order = await api('/orders', { method: 'POST', body: payload })
    const triage = await api(`/orders/${order.id}/triage`, { method: 'POST' })
    result.value = { ...triage, order_id: order.id, order_number: order.order_number || ('#' + order.id), internal: f.is_internal }
    loadOrders()
  } catch (e) {
    error.value = e.message || 'Nie udało się utworzyć zlecenia — sprawdź połączenie.'
  } finally {
    saving.value = false
  }
}

function goToOrders() {
  if (result.value?.order_id) focusOrderId.value = result.value.order_id
  emit('switch-tab', 'orders')
  resetForm()
}
</script>

<template>
  <div class="card create-card">
    <!-- SUCCESS -->
    <template v-if="result">
      <h2>Nowe zlecenie</h2>
      <div class="result-box">
        <div class="result-title">
          {{ result.internal ? 'Zlecenie wewnętrzne utworzone' : (RESULT_INFO[result.branch]?.title || 'Zlecenie utworzone') }}
        </div>
        <div class="result-sub">
          {{ result.internal ? 'Od razu w produkcji — arkusz gotowy do druku. Wycenę (operacje / materiały) uzupełnisz opcjonalnie.' : RESULT_INFO[result.branch]?.sub }}
          <template v-if="result.order_number"> &middot; nr {{ result.order_number }}</template>
        </div>
        <div v-if="result.message" class="result-msg">{{ result.message }}</div>
      </div>

      <div v-if="result.warnings?.length" class="warnings-panel">
        <div class="warnings-head">Uwagi (nie blokują zlecenia):</div>
        <div v-for="w in result.warnings" :key="w" class="warnings-item">• {{ w }}</div>
      </div>

      <p class="attach-hint">Załączniki (rysunek, PDF) dodasz w szczegółach zlecenia po jego utworzeniu.</p>

      <div class="actions">
        <button class="btn btn-primary" @click="goToOrders">Otwórz zlecenie →</button>
        <button class="btn btn-outline" @click="resetForm">+ Kolejne zlecenie</button>
      </div>
    </template>

    <!-- FORM -->
    <template v-else>
      <h2>Nowe zlecenie</h2>

      <div class="form-panel">
        <div class="form-group">
          <label>Kategoria zlecenia *</label>
          <div class="category-segment">
            <button
              v-for="category in CATEGORY_OPTIONS"
              :key="category.key"
              type="button"
              class="category-card"
              :class="{ active: form.order_type === category.key }"
              @click="selectCategory(category.key)"
            >
              {{ category.title }}
            </button>
          </div>
        </div>

        <div class="form-grid-3">
          <div class="form-group">
            <label>Nr zlecenia</label>
            <input v-model="form.order_number" placeholder="auto">
          </div>
          <div class="form-group">
            <label>Klient *</label>
            <input v-model="form.client" @input="onClientInput" list="clients-datalist" placeholder="Nazwa firmy / osoby">
            <datalist id="clients-datalist">
              <option v-for="c in uniqueClients" :key="c" :value="c" />
            </datalist>
            <div class="firm-quick">
              <span class="firm-quick-label">Wewnętrzne:</span>
              <button
                v-for="firm in INTERNAL_FIRMS"
                :key="firm"
                type="button"
                class="firm-chip"
                :class="{ active: form.is_internal && form.client === firm }"
                @click="selectFirm(firm)"
              >{{ firm }}</button>
            </div>
          </div>
          <div class="form-group">
            <label>Termin (kiedy gotowe?) *</label>
            <input type="date" v-model="form.deadline">
          </div>
        </div>

        <div class="form-group" style="margin-top:12px">
          <label>Opis zlecenia *</label>
          <textarea v-model="form.description" rows="4"
                    placeholder="Co trzeba zrobić? np. Pęknięte ramię koparki, blacha ok. 15 mm, dorobić i zespawać."></textarea>
        </div>

        <div class="form-grid-2" style="margin-top:12px">
          <div class="form-group">
            <label>Ilość sztuk *</label>
            <input type="number" v-model.number="form.quantity" min="1" placeholder="np. 1">
          </div>
          <div class="form-group">
            <label>Materiały</label>
            <div class="material-lines">
              <div v-for="(line, index) in form.materials_json" :key="index" class="material-line">
                <input v-model="line.name" list="materials-datalist" placeholder="np. S355, Hardox, żeliwo">
                <input type="number" v-model="line.qty_kg" min="0" step="0.1" placeholder="kg">
                <button type="button" class="line-remove" :disabled="form.materials_json.length === 1 && !line.name" @click="removeMaterialLine(index)">Usuń</button>
              </div>
            </div>
            <datalist id="materials-datalist">
              <option v-for="m in materialNames" :key="m" :value="m" />
            </datalist>
            <button type="button" class="btn-more-toggle material-add" @click="addMaterialLine">+ Dodaj materiał</button>
            <small class="field-hint">Pierwszy materiał będzie widoczny w liście zleceń.</small>
          </div>
        </div>
      </div>

      <button class="btn-more-toggle" @click="showMore = !showMore">
        {{ showMore ? '▲ Mniej opcji' : '▼ Więcej opcji' }}
      </button>

      <div v-if="showMore" class="form-panel">
        <div class="form-grid-2">
          <div class="form-group">
            <label>Szacowana wartość (PLN)</label>
            <input type="number" v-model.number="form.estimated_value" min="0" step="100" placeholder="np. 1500">
          </div>
        </div>
        <div class="form-group" style="margin-top:12px">
          <label>Uwagi wewnętrzne</label>
          <input v-model="form.notes" placeholder="np. PDF dosłany, pilne, kontakt...">
        </div>
        <div class="work-flags" style="margin-top:12px">
          <label class="work-flag">
            <input type="checkbox" v-model="form.has_drawing">
            <span>
              <strong>Rysunek / PDF jest</strong>
              <span>Technolog ma dokument do arkusza lub wyceny.</span>
            </span>
          </label>
          <label class="work-flag">
            <input type="checkbox" v-model="form.requires_visit">
            <span>
              <strong>Wymaga wizyty u klienta</strong>
              <span>Pomiary lub oględziny poza zakładem.</span>
            </span>
          </label>
          <label class="work-flag">
            <input type="checkbox" v-model="form.is_defence">
            <span>
              <strong>Projekt zastrzeżony</strong>
              <span>Dokumenty i oferty traktowane jako poufne.</span>
            </span>
          </label>
        </div>

        <div class="section-divider">Szczegóły wykonania i dostawa</div>

        <div class="form-grid-3" style="margin-top:4px">
          <div class="form-group">
            <label>Masa netto wyrobu (kg)</label>
            <input type="number" v-model="form.weight_kg" min="0" step="0.1" placeholder="całość zlecenia, np. 120">
          </div>
          <div class="form-group">
            <label>Nr rysunku</label>
            <input v-model="form.drawing_number" placeholder="np. RYS-2026/14">
          </div>
          <div class="form-group">
            <label>Wymiary</label>
            <input v-model="form.dimensions" placeholder="np. 20x60x10">
          </div>
        </div>
        <div class="form-group" style="margin-top:12px">
          <label>Adres dostawy</label>
          <textarea v-model="form.delivery_address" rows="2" placeholder=""></textarea>
        </div>
        <div class="form-group" style="margin-top:12px">
          <label>Osoba kontaktowa / tel.</label>
          <input v-model="form.contact" placeholder="Jan Kowalski, 600 100 200">
        </div>
      </div>

      <div v-if="error" class="error-banner">{{ error }}</div>

      <div class="actions">
        <button class="btn btn-primary" :disabled="!canSubmit || saving" @click="submit">
          {{ saving ? 'Tworzenie…' : 'Utwórz zlecenie' }}
        </button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.create-card {
  max-width: 1100px;
}

.form-grid-2,
.form-grid-3 {
  display: grid;
  gap: 12px;
}

.form-grid-2 {
  grid-template-columns: 1fr 1fr;
}

.form-grid-3 {
  grid-template-columns: 0.55fr 1.45fr 1fr;
}

.category-segment {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
  background: var(--sl-50);
}

.category-card {
  min-height: 38px;
  border: 0;
  border-right: 1px solid var(--border);
  border-radius: 0;
  background: transparent;
  color: var(--sl-700);
  padding: 8px 10px;
  cursor: pointer;
  font-size: 0.82rem;
  font-weight: 850;
}

.category-card:last-child {
  border-right: 0;
}

.category-card:hover {
  background: #fff;
  color: var(--rcm-blue);
}

.category-card.active {
  background: var(--rcm-blue);
  color: #fff;
}

.firm-quick {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 6px;
}
.firm-quick-label {
  font-size: 0.74rem;
  color: var(--muted);
  font-weight: 700;
}
.firm-chip {
  border: 1px solid var(--border);
  background: var(--sl-50);
  color: var(--sl-700);
  border-radius: 999px;
  padding: 3px 10px;
  font-size: 0.76rem;
  font-weight: 800;
  cursor: pointer;
}
.firm-chip:hover { border-color: var(--rcm-blue); color: var(--rcm-blue); }
.firm-chip.active {
  background: var(--rcm-blue);
  border-color: var(--rcm-blue);
  color: #fff;
}

.material-lines {
  display: grid;
  gap: 8px;
}

.material-line {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 90px auto;
  gap: 8px;
  align-items: center;
}

.line-remove {
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--card);
  color: var(--muted);
  min-height: 38px;
  padding: 0 10px;
  cursor: pointer;
  font-weight: 800;
}

.line-remove:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.material-add {
  margin: 4px 0 0;
}

.btn-more-toggle {
  background: none;
  border: none;
  color: var(--rcm-accent);
  font-size: 0.85rem;
  cursor: pointer;
  padding: 6px 0;
  margin: 6px 0;
  display: flex;
  align-items: center;
  gap: 4px;
}
.btn-more-toggle:hover { text-decoration: underline; }

.actions {
  display: flex;
  gap: 8px;
  margin-top: 16px;
  flex-wrap: wrap;
}

.error-banner {
  color: var(--rcm-red);
  background: rgba(198, 40, 40, 0.08);
  border: 1px solid var(--rcm-red);
  border-radius: 6px;
  padding: 10px 14px;
  margin-top: 12px;
  font-size: 0.9rem;
}

.result-box {
  border: 1px solid var(--border);
  border-left: 3px solid var(--green);
  border-radius: var(--radius);
  padding: 14px 16px;
  background: var(--sl-50);
}
.result-title { font-size: 1rem; font-weight: 700; color: var(--text); }
.result-sub   { font-size: 0.88rem; color: var(--muted); margin-top: 4px; }
.result-msg   { font-size: 0.85rem; color: var(--text); margin-top: 8px; }

.attach-hint {
  font-size: 0.83rem;
  color: var(--muted);
  margin: 14px 0 0;
}

.warnings-panel {
  margin-top: 12px;
  padding: 12px 16px;
  background: rgba(255, 193, 7, 0.08);
  border: 1px solid var(--rcm-warn);
  border-radius: var(--radius);
}
.warnings-head { font-weight: 700; font-size: 0.85rem; color: var(--rcm-warn); margin-bottom: 6px; }
.warnings-item { font-size: 0.88rem; color: var(--text); margin-bottom: 2px; }

.section-divider {
  margin-top: 18px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
  font-size: 0.78rem;
  font-weight: 800;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

@media (max-width: 600px) {
  .form-grid-2,
  .form-grid-3,
  .category-segment { grid-template-columns: 1fr; }

  .material-line { grid-template-columns: 1fr; }

  .category-card {
    border-right: 0;
    border-bottom: 1px solid var(--border);
  }

  .category-card:last-child {
    border-bottom: 0;
  }
}
</style>
