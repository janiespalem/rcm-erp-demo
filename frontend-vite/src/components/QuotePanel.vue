<script setup>
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { api } from '@/composables/useApi'
import { useOrders } from '@/composables/useOrders'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { useSettings } from '@/composables/useSettings'
import { moneyLabel } from '@/utils/format'

const props = defineProps({
  order: Object,
  showClose: { type: Boolean, default: true },
})
const emit = defineEmits(['close', 'saved'])

const { loadOrders } = useOrders()
const { approvedMaterials } = useApprovedMaterials()
const { settings } = useSettings()

const isLoading = ref(['quoted', 'in_production', 'w_trakcie', 'gotowe', 'wydane'].includes(props.order?.status))
const isSaving = ref(false)
const saveError = ref(false)
const operationCatalog = ref([])
const suggestedOperations = ref([])
const priceMode = ref('structured')

const processes = ref([])
const materials = ref([])
const labor_hours = ref(0)
const overhead_pct = ref(0.1)
const margin_pct = ref(0.25)
const transport_cost = ref(0)
const manual_total_net = ref('')
const show_unit_prices = ref(true)

const previewResult = ref(null)
const previewError = ref(false)
let previewTimer = null

const orderTitle = computed(() => props.order?.order_number || `#${props.order?.id}`)
const quantity = computed(() => Number(props.order?.quantity || 1))
function materialLineTotal(line) {
  const cost = Number(line.cost || 0)
  if (cost > 0) return cost
  return Number(line.qty_kg || 0) * Number(line.price_per_kg || 0)
}
const materialTotal = computed(() =>
  materials.value.reduce((sum, line) => sum + materialLineTotal(line), 0)
)
const totalMaterialWeight = computed(() =>
  materials.value.reduce((sum, line) => sum + Number(line.qty_kg || 0), 0)
)
const approvedMaterialNames = computed(() =>
  [...approvedMaterials.value]
    .map(m => m.name)
    .sort((a, b) => a.localeCompare(b, 'pl', { numeric: true }))
)
const operationsTotal = computed(() =>
  processes.value.reduce((sum, op) => {
    const hoursCost = Number(op.hours || 0) * Number(op.rate_per_hour || 0)
    return sum + (hoursCost || Number(op.cost || 0))
  }, 0)
)
const extraLaborTotal = computed(() => Number(labor_hours.value || 0) * Number(settings.value.labor_rate_pln || 0))
const baseTotal = computed(() => operationsTotal.value + materialTotal.value + extraLaborTotal.value)
const fallbackTotal = computed(() =>
  Math.round(baseTotal.value * (1 + Number(overhead_pct.value || 0)) * (1 + Number(margin_pct.value || 0)) + Number(transport_cost.value || 0))
)
const previewTotal = computed(() => {
  if (priceMode.value === 'manual') return Number(manual_total_net.value || 0)
  return previewResult.value?.total_net ?? fallbackTotal.value
})
const canSave = computed(() =>
  priceMode.value === 'manual'
    ? Number(manual_total_net.value) > 0
    : previewTotal.value > 0 || baseTotal.value > 0
)
const operationDepartments = computed(() =>
  [...new Set(operationCatalog.value.map(op => op.department).filter(Boolean))].sort()
)

function materialRateForOrder(order) {
  if (!order?.material) return 0
  const key = String(order.material || '').toLowerCase()
  const mat = approvedMaterials.value.find(m => {
    const name = String(m.name || '').toLowerCase()
    return key === name || key.includes(name) || name.includes(key)
  })
  return mat ? Number(mat.default_rate_pln_kg || 0) : 0
}

async function loadSuggestions(order) {
  const params = new URLSearchParams()
  const text = [order?.description, order?.notes, order?.sop_name, order?.order_type].filter(Boolean).join(' ')
  if (text) params.append('text', text)
  if (order?.material) params.append('material', order.material)
  try {
    suggestedOperations.value = await api(`/operation-catalog/suggest?${params.toString()}`)
  } catch {
    suggestedOperations.value = []
  }
}

function addOperation(op = {}) {
  processes.value.push({
    name: op.name || '',
    department: op.department || '',
    hours: Number(op.hours || 0),
    rate_per_hour: Number(op.default_rate || op.rate_per_hour || settings.value.labor_rate_pln || 0),
    cost: Number(op.cost || 0),
    material: props.order?.material || '',
  })
}

function removeOperation(index) {
  processes.value.splice(index, 1)
}

function addMaterial(line = {}) {
  materials.value.push({
    name: line.name || '',
    qty_kg: Number(line.qty_kg || 0),
    price_per_kg: Number(line.price_per_kg || 0),
    cost: Number(line.cost || 0),
  })
}

function removeMaterial(index) {
  materials.value.splice(index, 1)
}

async function fetchPreview() {
  if (priceMode.value !== 'structured') return
  previewError.value = false
  try {
    previewResult.value = await api('/quotes/preview', {
      method: 'POST',
      body: structuredPayload(),
    })
  } catch {
    previewError.value = true
  }
}

function schedulePreview() {
  if (priceMode.value !== 'structured') return
  previewResult.value = null
  previewError.value = false
  clearTimeout(previewTimer)
  previewTimer = setTimeout(fetchPreview, 350)
}

function structuredPayload() {
  return {
    processes: processes.value,
    materials: materials.value,
    // Legacy single fields sent as aggregates so backward-compat consumers
    // (PDF, save-as-template) still see a total weight. Pricing uses `materials`.
    material_weight_kg: totalMaterialWeight.value,
    material_price_per_kg: 0,
    material_cost: 0,
    labor_hours: Number(labor_hours.value || 0),
    overhead_pct: Number(overhead_pct.value || 0),
    margin_pct: Number(margin_pct.value || 0),
    transport_cost: Number(transport_cost.value || 0),
  }
}

async function saveQuote() {
  if (isSaving.value || !canSave.value) return
  isSaving.value = true
  saveError.value = false
  try {
    if (priceMode.value === 'manual') {
      await api(`/orders/${props.order.id}/quote/manual`, {
        method: 'POST',
        body: { total_net: Number(manual_total_net.value) },
      })
    } else {
      await api(`/orders/${props.order.id}/quote/structured`, {
        method: 'POST',
        body: {
          ...structuredPayload(),
          weight_netto_kg: totalMaterialWeight.value,
          weight_brutto_kg: totalMaterialWeight.value,
          show_unit_prices: show_unit_prices.value,
        },
      })
    }
    await loadOrders()
    emit('saved')
  } catch {
    saveError.value = true
  } finally {
    isSaving.value = false
  }
}

async function loadExistingQuote() {
  if (!['quoted', 'in_production', 'w_trakcie', 'gotowe', 'wydane'].includes(props.order?.status)) return
  try {
    const quote = await api(`/orders/${props.order.id}/quote`)
    priceMode.value = quote.estimate_version === 'manual' ? 'manual' : 'structured'
    manual_total_net.value = quote.total_net || ''
    processes.value = Array.isArray(quote.processes_json)
      ? quote.processes_json.map(op => ({
          ...op,
          department: op.department || op.wydział || '',
          material: op.material || props.order?.material || '',
        }))
      : []
    if (Array.isArray(quote.materials_json) && quote.materials_json.length) {
      materials.value = quote.materials_json.map(m => ({
        name: m.name || m.material || '',
        qty_kg: Number(m.qty_kg || 0),
        price_per_kg: Number(m.price_per_kg || 0),
        cost: Number(m.cost || 0),
      }))
    } else {
      // Old quote without material rows — rebuild a single row from legacy fields.
      const w = Number(quote.material_weight_kg || 0)
      const p = Number(quote.material_price_per_kg || materialRateForOrder(props.order) || 0)
      const c = Number(quote.material_cost || 0)
      materials.value = (w > 0 || p > 0 || c > 0)
        ? [{ name: props.order?.material || '', qty_kg: w, price_per_kg: p, cost: (w > 0 && p > 0) ? 0 : c }]
        : []
    }
    labor_hours.value = Number(quote.labor_hours || 0)
    overhead_pct.value = Number(quote.overhead_pct ?? 0.1)
    margin_pct.value = Number(quote.margin_pct ?? 0.25)
    transport_cost.value = Number(quote.transport_cost || 0)
    show_unit_prices.value = quote.show_unit_prices !== false
  } catch {
    materials.value = []
  }
}

onMounted(async () => {
  try {
    operationCatalog.value = await api('/operation-catalog/')
  } catch {
    operationCatalog.value = []
  }
  await loadExistingQuote()
  await loadSuggestions(props.order)
  if (!processes.value.length) addOperation()
  if (!materials.value.length) {
    addMaterial({ name: props.order?.material || '', price_per_kg: materialRateForOrder(props.order) })
  }
  isLoading.value = false
  schedulePreview()
})

onUnmounted(() => clearTimeout(previewTimer))

watch(
  [priceMode, processes, materials, labor_hours, overhead_pct, margin_pct, transport_cost],
  schedulePreview,
  { deep: true },
)
</script>

<template>
  <section class="quote-panel" aria-label="Wycena">
    <header class="quote-header">
      <div>
        <button v-if="showClose" class="close-link" type="button" @click="emit('close')">Zamknij</button>
        <h2>Wycena {{ orderTitle }}</h2>
        <p>{{ order?.client || 'Bez klienta' }}</p>
      </div>
      <div class="mode-switch">
        <button type="button" :class="{ active: priceMode === 'structured' }" @click="priceMode = 'structured'">Panel</button>
        <button type="button" :class="{ active: priceMode === 'manual' }" @click="priceMode = 'manual'">Ręczna</button>
      </div>
    </header>

    <div v-if="isLoading" class="loading-state">Ładowanie wyceny...</div>

    <div v-else class="quote-layout">
      <main class="quote-main">
        <section class="order-context">
          <div>
            <span>Termin</span>
            <strong>{{ order?.deadline ? order.deadline.split('-').reverse().join('.') : '—' }}</strong>
          </div>
          <div>
            <span>Ilość</span>
            <strong>{{ quantity }} szt.</strong>
          </div>
          <div>
            <span>Materiał</span>
            <strong>{{ order?.material || '—' }}</strong>
          </div>
          <p v-if="order?.description">{{ order.description }}</p>
        </section>

        <template v-if="priceMode === 'structured'">
          <section class="quote-block">
            <div class="block-head">
              <h3>Operacje</h3>
              <button type="button" @click="addOperation()">Dodaj operację</button>
            </div>
            <datalist id="quote-departments">
              <option v-for="dept in operationDepartments" :key="dept" :value="dept" />
            </datalist>
            <div class="ops-table">
              <div class="ops-head">
                <span>Operacja</span>
                <span>Wydział</span>
                <span>h</span>
                <span>PLN/h</span>
                <span></span>
              </div>
              <div v-for="(operation, index) in processes" :key="index" class="ops-row">
                <input v-model="operation.name" placeholder="np. Cięcie, spawanie">
                <input v-model="operation.department" list="quote-departments" placeholder="wydział">
                <input type="number" v-model.number="operation.hours" min="0" step="0.25">
                <input type="number" v-model.number="operation.rate_per_hour" min="0" step="5">
                <button type="button" @click="removeOperation(index)">Usuń</button>
              </div>
            </div>
            <div v-if="suggestedOperations.length" class="suggestions">
              <button v-for="op in suggestedOperations.slice(0, 6)" :key="op.id" type="button" @click="addOperation(op)">
                + {{ op.name }}
              </button>
            </div>
          </section>

          <section class="quote-block">
            <div class="block-head">
              <h3>Materiały</h3>
              <button type="button" @click="addMaterial()">Dodaj materiał</button>
            </div>
            <datalist id="quote-materials">
              <option v-for="m in approvedMaterialNames" :key="m" :value="m" />
            </datalist>
            <div class="mat-table">
              <div class="mat-head">
                <span>Materiał</span>
                <span>kg</span>
                <span>PLN/kg</span>
                <span>Koszt PLN</span>
                <span></span>
              </div>
              <div v-for="(line, index) in materials" :key="index" class="mat-row">
                <input v-model="line.name" list="quote-materials" placeholder="np. S235, Hardox">
                <input type="number" v-model.number="line.qty_kg" min="0" step="0.5">
                <input type="number" v-model.number="line.price_per_kg" min="0" step="0.1">
                <input type="number" v-model.number="line.cost" min="0" step="10" placeholder="auto">
                <button type="button" @click="removeMaterial(index)">Usuń</button>
              </div>
              <p v-if="!materials.length" class="mat-empty">Brak materiałów — dodaj pozycję.</p>
            </div>
          </section>

          <section class="quote-block">
            <div class="block-head">
              <h3>Robocizna i narzuty</h3>
            </div>
            <div class="input-grid">
              <label>
                <span>Robocizna dodatkowa h</span>
                <input type="number" v-model.number="labor_hours" min="0" step="0.25">
              </label>
              <label>
                <span>Overhead</span>
                <input type="number" v-model.number="overhead_pct" min="0" max="1" step="0.05">
              </label>
              <label>
                <span>Marża</span>
                <input type="number" v-model.number="margin_pct" min="0" max="2" step="0.05">
              </label>
              <label>
                <span>Transport PLN</span>
                <input type="number" v-model.number="transport_cost" min="0" step="50">
              </label>
              <label class="checkbox-line">
                <input type="checkbox" v-model="show_unit_prices">
                <span>Pokaż ceny operacji na ofercie</span>
              </label>
            </div>
          </section>
        </template>

        <section v-else class="quote-block manual-block">
          <h3>Cena ręczna</h3>
          <label>
            <span>Cena netto PLN</span>
            <input type="number" v-model.number="manual_total_net" min="0" step="0.01">
          </label>
        </section>
      </main>

      <aside class="quote-summary">
        <h3>Podsumowanie</h3>
        <div class="summary-row">
          <span>Operacje</span>
          <strong>{{ moneyLabel(operationsTotal) }}</strong>
        </div>
        <div class="summary-row">
          <span>Materiał</span>
          <strong>{{ moneyLabel(materialTotal) }}</strong>
        </div>
        <div class="summary-row">
          <span>Dodatkowa robocizna</span>
          <strong>{{ moneyLabel(extraLaborTotal) }}</strong>
        </div>
        <div class="summary-row">
          <span>Transport</span>
          <strong>{{ moneyLabel(transport_cost) }}</strong>
        </div>
        <div class="summary-total">
          <span>Netto</span>
          <strong>{{ moneyLabel(previewTotal) }}</strong>
        </div>
        <div class="summary-row">
          <span>Brutto 23%</span>
          <strong>{{ moneyLabel(previewTotal * 1.23) }}</strong>
        </div>
        <div v-if="quantity > 1" class="summary-row">
          <span>Netto / szt.</span>
          <strong>{{ moneyLabel(previewTotal / quantity) }}</strong>
        </div>
        <p v-if="previewError" class="preview-error">Podgląd z backendu niedostępny. Pokazuję lokalny szacunek.</p>
        <p v-if="saveError" class="preview-error">Nie udało się zapisać wyceny. Spróbuj ponownie.</p>
        <button class="btn btn-success" type="button" :disabled="isSaving || !canSave" @click="saveQuote">
          {{ isSaving ? 'Zapisywanie...' : 'Zapisz wycenę i wróć' }}
        </button>
      </aside>
    </div>
  </section>
</template>

<style scoped>
.quote-panel {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
}

.quote-header {
  position: sticky;
  top: 0;
  z-index: 2;
  border-bottom: 1px solid var(--border);
  background: #fff;
  padding: 16px 20px;
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: flex-start;
}

.quote-header h2 {
  margin: 6px 0 0;
  color: var(--rcm-blue);
  font-size: 1.15rem;
  font-weight: 850;
}

.quote-header p {
  margin: 2px 0 0;
  color: var(--muted);
  font-size: 0.88rem;
}

.close-link {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 5px 10px;
  cursor: pointer;
  font-size: 0.76rem;
  font-weight: 800;
}

.mode-switch {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--sl-50);
  padding: 3px;
  gap: 3px;
}

.mode-switch button {
  border: none;
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  padding: 6px 11px;
  cursor: pointer;
  font-weight: 850;
  font-size: 0.8rem;
}

.mode-switch button.active {
  background: var(--rcm-blue);
  color: #fff;
}

.loading-state {
  padding: 52px 20px;
  color: var(--muted);
  text-align: center;
}

.quote-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: 18px;
  padding: 18px 20px 20px;
}

.quote-main {
  display: grid;
  gap: 14px;
  align-content: start;
}

.order-context,
.quote-block,
.quote-summary {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
}

.order-context {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
  padding: 13px;
  background: var(--sl-50);
}

.order-context p {
  grid-column: 1 / -1;
  color: var(--sl-700);
  font-size: 0.86rem;
  line-height: 1.45;
}

.order-context span,
.input-grid span,
.manual-block span {
  display: block;
  color: var(--muted);
  font-size: 0.72rem;
  font-weight: 850;
  text-transform: uppercase;
}

.order-context strong {
  display: block;
  margin-top: 3px;
  color: var(--text);
  font-size: 0.9rem;
}

.quote-block {
  padding: 14px;
}

.block-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: center;
  margin-bottom: 12px;
}

.quote-block h3,
.quote-summary h3 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 0.95rem;
  font-weight: 850;
}

.block-head button,
.suggestions button {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 5px 9px;
  cursor: pointer;
  font-size: 0.74rem;
  font-weight: 850;
}

.ops-table {
  display: grid;
  gap: 7px;
}

.ops-head,
.ops-row {
  display: grid;
  grid-template-columns: minmax(160px, 1fr) 130px 80px 95px 64px;
  gap: 7px;
  align-items: center;
}

.ops-head {
  color: var(--muted);
  font-size: 0.68rem;
  font-weight: 850;
  text-transform: uppercase;
}

.ops-row input,
.mat-row input,
.input-grid input,
.manual-block input {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: #fff;
  color: var(--text);
  padding: 8px 9px;
  font: inherit;
  font-size: 0.85rem;
}

.ops-row button,
.mat-row button {
  border: 1px solid var(--rcm-red);
  border-radius: 7px;
  background: #fff;
  color: var(--rcm-red);
  padding: 7px 8px;
  cursor: pointer;
  font-size: 0.72rem;
  font-weight: 850;
}

.mat-table {
  display: grid;
  gap: 7px;
}

.mat-head,
.mat-row {
  display: grid;
  grid-template-columns: minmax(140px, 1fr) 80px 95px 100px 64px;
  gap: 7px;
  align-items: center;
}

.mat-head {
  color: var(--muted);
  font-size: 0.68rem;
  font-weight: 850;
  text-transform: uppercase;
}

.mat-empty {
  color: var(--muted);
  font-size: 0.82rem;
  padding: 4px 2px;
}

.suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 11px;
}

.input-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 11px;
}

.input-grid label,
.manual-block label {
  display: grid;
  gap: 5px;
}

.checkbox-line {
  display: flex !important;
  align-items: center;
  gap: 8px;
  min-height: 38px;
}

.checkbox-line input {
  width: auto;
}

.manual-block {
  display: grid;
  gap: 12px;
}

.manual-block input {
  max-width: 240px;
  border-width: 2px;
  border-color: var(--rcm-green);
  font-size: 1.3rem;
  font-weight: 850;
}

.quote-summary {
  position: sticky;
  top: 92px;
  display: grid;
  gap: 9px;
  align-content: start;
  padding: 15px;
}

.summary-row,
.summary-total {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  color: var(--muted);
  font-size: 0.84rem;
}

.summary-row strong {
  color: var(--text);
}

.summary-total {
  border-top: 1px solid var(--border);
  margin-top: 4px;
  padding-top: 12px;
  color: var(--text);
  align-items: baseline;
}

.summary-total strong {
  color: var(--rcm-blue);
  font-family: var(--font-data);
  font-size: 1.55rem;
}

.preview-error {
  border-radius: 7px;
  background: rgba(220, 38, 38, 0.09);
  color: var(--rcm-red);
  padding: 8px;
  font-size: 0.78rem;
  font-weight: 750;
}

.quote-summary .btn {
  justify-content: center;
}

@media (max-width: 940px) {
  .quote-layout {
    grid-template-columns: 1fr;
  }

  .quote-summary {
    position: static;
  }

  .ops-head,
  .mat-head {
    display: none;
  }

  .ops-row,
  .mat-row,
  .input-grid,
  .order-context {
    grid-template-columns: 1fr;
  }
}
</style>
