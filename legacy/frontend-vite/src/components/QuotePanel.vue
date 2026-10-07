<script setup>
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import { api, openPdf, readCache, writeCache } from '@/composables/useApi'
import { useOrders } from '@/composables/useOrders'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { useOperationCatalog } from '@/composables/useOperationCatalog'
import { useSettings } from '@/composables/useSettings'
import { moneyLabel } from '@/utils/format'

const props = defineProps({
  order: Object,
  showClose: { type: Boolean, default: true },
})
const emit = defineEmits(['close', 'saved', 'history'])

const { loadOrders } = useOrders()
const { approvedMaterials } = useApprovedMaterials()
const { settings } = useSettings()

const isLoading = ref(['quoted', 'in_production', 'gotowe', 'wydane'].includes(props.order?.status))
const isSaving = ref(false)
const justSaved = ref(false)
const saveError = ref(false)
const dirty = ref(false)

// Guard the close so an accidental click doesn't silently drop an unsaved quote.
// ponytail: native confirm — no bespoke modal for a one-line confirmation.
function requestClose() {
  if (dirty.value && !window.confirm('Masz niezapisane zmiany w wycenie. Zamknąć bez zapisu?')) return
  emit('close')
}
const { operationCatalog, loadOperationCatalog } = useOperationCatalog()
const suggestedOperations = ref([])
const priceMode = ref('kalkulacja')

const processes = ref([])
const materials = ref([])
const labor_hours = ref(0)
// overhead/marża trzymane w PROCENTACH (UI), do payloadu/obliczeń dzielone /100
const overhead_pct = ref(10)
const margin_pct = ref(25)
const transport_cost = ref(0)
const manual_total_net = ref('')
const show_unit_prices = ref(true)

const weight_rate_pln_kg = ref(0)
const weight_netto_kg = ref(0)
const weight_brutto_kg = ref(0)
const weightBasis = ref('netto')
// Masa do wyceny od masy domyślnie z materiału (brutto = Σ kg pozycji Materiały).
const weightFromMaterial = ref(true)

const previewResult = ref(null)
const previewError = ref(false)
let previewTimer = null
let previewGeneration = 0

const orderTitle = computed(() => props.order?.order_number || `#${props.order?.id}`)
const quantity = computed(() => Number(props.order?.quantity || 1))
// Zlecenie wewnętrzne (własna firma): liczymy koszt własny (materiał + robocizna),
// bez marży/VAT/oferty. Marża/narzut zostają dostępne, ale schowane (opcjonalne).
const isInternal = computed(() => !!props.order?.is_internal)
const showMarkup = ref(false)
function materialLineTotal(line) {
  if (line.cost_override) return Number(line.cost || 0)
  const cost = Number(line.cost || 0)
  if (cost > 0 && (!Number(line.qty_kg || 0) || !Number(line.price_per_kg || 0))) return cost
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
    const hasRateFields = Object.hasOwn(op, 'hours') || Object.hasOwn(op, 'rate_per_hour')
    return sum + (hasRateFields
      ? Number(op.hours || 0) * Number(op.rate_per_hour || 0)
      : Number(op.cost || 0))
  }, 0)
)
const extraLaborTotal = computed(() => Number(labor_hours.value || 0) * Number(settings.value.labor_rate_pln || 0))
const pricingWeight = computed(() =>
  weightBasis.value === 'brutto'
    ? Number(weight_brutto_kg.value || materialBruttoKg.value || 0)
    : Number(weight_netto_kg.value || 0)
)
const baseTotal = computed(() => operationsTotal.value + materialTotal.value + extraLaborTotal.value)
const fallbackTotal = computed(() =>
  priceMode.value === 'od_masy'
    ? Math.round(pricingWeight.value * Number(weight_rate_pln_kg.value || 0) + Number(transport_cost.value || 0))
    : Math.round(baseTotal.value * (1 + Number(overhead_pct.value || 0) / 100) * (1 + Number(margin_pct.value || 0) / 100) + Number(transport_cost.value || 0))
)
const previewTotal = computed(() => {
  if (priceMode.value === 'manual') return Number(manual_total_net.value || 0)
  return previewResult.value?.total_net ?? fallbackTotal.value
})
const canSave = computed(() =>
  priceMode.value === 'manual'
    ? Number(manual_total_net.value) > 0
    : priceMode.value === 'od_masy'
      ? pricingWeight.value > 0 && Number(weight_rate_pln_kg.value || 0) > 0
      : previewTotal.value > 0 || baseTotal.value > 0
)
const operationDepartments = computed(() =>
  [...new Set(operationCatalog.value.map(op => op.department).filter(Boolean))].sort()
)

// Derived mass balance — read-only, no inputs, no backend calls.
const materialBruttoKg = computed(() =>
  materials.value.reduce((sum, line) => sum + Number(line.qty_kg || 0), 0)
)
const odpadKg = computed(() =>
  Math.max(0, materialBruttoKg.value - Number(weight_netto_kg.value || 0))
)
const odpadPct = computed(() =>
  materialBruttoKg.value > 0 ? odpadKg.value / materialBruttoKg.value : 0
)

function materialRateForName(name) {
  if (!name) return 0
  const key = String(name || '').toLowerCase()
  const mat = approvedMaterials.value.find(m => {
    const name = String(m.name || '').toLowerCase()
    return key === name || key.includes(name) || name.includes(key)
  })
  return mat ? Number(mat.default_rate_pln_kg || 0) : 0
}

function materialRateForOrder(order) {
  return materialRateForName(order?.material)
}

function orderMaterialLines(order) {
  if (Array.isArray(order?.materials_json) && order.materials_json.length) {
    return order.materials_json
      .map(line => {
        const name = line.name || line.material || line.mat || ''
        return {
          name,
          qty_kg: Number(line.qty_kg || line.qty || 0),
          price_per_kg: Number(line.price_per_kg || line.default_rate_pln_kg || materialRateForName(name) || 0),
        }
      })
      .filter(line => line.name)
  }
  return order?.material
    ? [{ name: order.material, qty_kg: 0, price_per_kg: materialRateForOrder(order) }]
    : []
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
    cost_override: !!line.cost_override,
  })
}

function removeMaterial(index) {
  materials.value.splice(index, 1)
}

async function fetchPreview(generation) {
  if (priceMode.value === 'manual') return
  previewError.value = false
  try {
    const result = await api('/quotes/preview', {
      method: 'POST',
      body: structuredPayload(),
    })
    if (generation === previewGeneration) previewResult.value = result
  } catch {
    if (generation === previewGeneration) previewError.value = true
  }
}

function schedulePreview() {
  if (priceMode.value === 'manual') return
  const generation = ++previewGeneration
  previewResult.value = null
  previewError.value = false
  clearTimeout(previewTimer)
  previewTimer = setTimeout(() => fetchPreview(generation), 350)
}

function structuredPayload() {
  return {
    method: priceMode.value,
    weight_basis: weightBasis.value,
    processes: processes.value,
    materials: materials.value.map(line => ({
      name: line.name,
      qty_kg: Number(line.qty_kg || 0),
      price_per_kg: Number(line.price_per_kg || 0),
      cost: line.cost_override ? Number(line.cost || 0) : 0,
    })),
    // Legacy single fields sent as aggregates so backward-compat consumers
    // (PDF, save-as-template) still see a total weight. Pricing uses `materials`.
    material_weight_kg: totalMaterialWeight.value,
    material_price_per_kg: 0,
    material_cost: 0,
    labor_hours: Number(labor_hours.value || 0),
    overhead_pct: Number(overhead_pct.value || 0) / 100,
    margin_pct: Number(margin_pct.value || 0) / 100,
    transport_cost: Number(transport_cost.value || 0),
    weight_kg: priceMode.value === 'od_masy' ? pricingWeight.value : 0,
    weight_rate_pln_kg: Number(weight_rate_pln_kg.value || 0),
    weight_netto_kg: Number(weight_netto_kg.value || 0),
    weight_brutto_kg: Number(weight_brutto_kg.value || materialBruttoKg.value || 0),
  }
}

async function saveQuote() {
  if (isSaving.value || !canSave.value) return
  isSaving.value = true
  saveError.value = false
  try {
    let saved
    if (priceMode.value === 'manual') {
      saved = await api(`/orders/${props.order.id}/quote/manual`, {
        method: 'POST',
        body: { total_net: Number(manual_total_net.value) },
      })
    } else {
      saved = await api(`/orders/${props.order.id}/quote/structured`, {
        method: 'POST',
        body: {
          ...structuredPayload(),
          show_unit_prices: show_unit_prices.value,
        },
      })
    }
    if (saved) writeCache('quote_' + props.order.id, saved)
    await loadOrders()
    dirty.value = false
    // Nie zamykaj od razu — pokaż krok "wydrukuj arkusz" (po to jest wycena).
    justSaved.value = true
  } catch {
    saveError.value = true
  } finally {
    isSaving.value = false
  }
}

function printArkusz() {
  openPdf(`/orders/${props.order.id}/pdf`)
}

// Czy wczytano realnie zapisaną wycenę (nie mylić ze statusem — wewnętrzne są
// od razu in_production, ale mogą NIE mieć jeszcze wyceny).
let loadedQuote = false

function applyQuote(quote) {
    loadedQuote = true
    const method = quote.pricing_method || quote.estimate_version
    priceMode.value = method === 'od_masy' ? 'od_masy' : method === 'reczna' || method === 'manual' ? 'manual' : 'kalkulacja'
    weightBasis.value = quote.weight_basis || 'netto'
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
        cost_override: Number(m.cost || 0) > 0,
      }))
    } else {
      // Old quote without material rows — rebuild a single row from legacy fields.
      const w = Number(quote.material_weight_kg || 0)
      const p = Number(quote.material_price_per_kg || materialRateForOrder(props.order) || 0)
      const c = Number(quote.material_cost || 0)
      materials.value = (w > 0 || p > 0 || c > 0)
        ? [{ name: props.order?.material || '', qty_kg: w, price_per_kg: p, cost: (w > 0 && p > 0) ? 0 : c, cost_override: !(w > 0 && p > 0) && c > 0 }]
        : []
    }
    labor_hours.value = Number(quote.labor_hours || 0)
    // DB trzyma ułamki (0.1) → UI w procentach (10).
    overhead_pct.value = Number(quote.overhead_pct ?? 0.1) * 100
    margin_pct.value = Number(quote.margin_pct ?? 0.25) * 100
    transport_cost.value = Number(quote.transport_cost || 0)
    show_unit_prices.value = quote.show_unit_prices !== false
    // Zapisana wycena: zachowaj zapisaną masę (nie nadpisuj z materiału).
    weightFromMaterial.value = false
    weight_rate_pln_kg.value = Number(quote.weight_rate_pln_kg || 0)
    weight_netto_kg.value = Number(quote.weight_netto_kg || 0)
    weight_brutto_kg.value = Number(quote.weight_brutto_kg || 0)
    const savedPricingWeight = Number(quote.weight_kg || 0)
    if (savedPricingWeight > 0 && weightBasis.value === 'netto' && !weight_netto_kg.value) {
      weight_netto_kg.value = savedPricingWeight
    }
    if (savedPricingWeight > 0 && weightBasis.value === 'brutto' && !weight_brutto_kg.value) {
      weight_brutto_kg.value = savedPricingWeight
    }
}

async function loadExistingQuote() {
  if (!['quoted', 'in_production', 'gotowe', 'wydane'].includes(props.order?.status)) return
  const key = 'quote_' + props.order.id
  // Cache-first: pokaż ostatnio zapisaną wycenę natychmiast, świeżą dograj w tle.
  const cached = readCache(key)
  if (cached) { applyQuote(cached); isLoading.value = false }
  try {
    // quiet: brak wyceny (404) to normalny stan — otwieramy panel, by ją utworzyć.
    const quote = await api(`/orders/${props.order.id}/quote`, { quiet: true })
    if (!dirty.value) applyQuote(quote)
    writeCache(key, quote)
  } catch {
    if (!cached) materials.value = []
  }
}

onMounted(async () => {
  // Katalog operacji z singletona (cache + revalidacja w tle) — nie blokuje otwarcia.
  loadOperationCatalog().catch(() => {})
  const hadExistingQuote = ['quoted', 'in_production', 'gotowe', 'wydane'].includes(props.order?.status)
  await loadExistingQuote()
  // Wewnętrzne: bez marży/narzutu, DOPÓKI nie ma realnie zapisanej wyceny z takimi
  // wartościami. Gate na loadedQuote, nie na statusie — inaczej wewnętrzne w
  // produkcji dostawały domyślne 10%/25% doliczone do total_net.
  if (isInternal.value && !loadedQuote) {
    overhead_pct.value = 0
    margin_pct.value = 0
    show_unit_prices.value = false
  }
  await loadSuggestions(props.order)
  if (!processes.value.length) addOperation()
  if (!materials.value.length) {
    const intakeMaterials = !hadExistingQuote ? orderMaterialLines(props.order) : []
    if (intakeMaterials.length) intakeMaterials.forEach(addMaterial)
    else addMaterial({ name: props.order?.material || '', price_per_kg: materialRateForOrder(props.order) })
  }
  // Pre-fill net weight from order intake data for fresh (not yet quoted) orders.
  if (!hadExistingQuote && props.order?.weight_kg) {
    weight_netto_kg.value = Number(props.order.weight_kg)
  }
  isLoading.value = false
  schedulePreview()
  // Ignore field changes made during load/prefill; only user edits mark dirty.
  await nextTick()
  dirty.value = false
})

onUnmounted(() => clearTimeout(previewTimer))

// "Masa z materiału": brutto = Σ kg pozycji Materiały.
watch([materialBruttoKg, weightFromMaterial], () => {
  if (weightFromMaterial.value && materialBruttoKg.value > 0) {
    weight_brutto_kg.value = Number(materialBruttoKg.value)
  }
})

watch(
  [priceMode, processes, materials, labor_hours, overhead_pct, margin_pct, transport_cost,
   weight_netto_kg, weight_brutto_kg, weight_rate_pln_kg, weightBasis, weightFromMaterial, manual_total_net, show_unit_prices],
  () => { schedulePreview(); dirty.value = true; justSaved.value = false },
  { deep: true },
)
</script>

<template>
  <section class="quote-panel" aria-label="Wycena">
    <header class="quote-header">
      <div>
        <div class="header-actions">
          <button v-if="showClose" class="close-link" type="button" @click="requestClose()">Zamknij wycenę</button>
          <button class="close-link" type="button" @click="emit('history')">🕘 Historia</button>
        </div>
        <h2>Wycena {{ orderTitle }}</h2>
        <p>
          {{ order?.client || 'Bez klienta' }}
          <span v-if="isInternal" class="internal-tag">wewnętrzne</span>
        </p>
      </div>
      <div class="mode-switch">
        <button type="button" :class="{ active: priceMode === 'kalkulacja' }" @click="priceMode = 'kalkulacja'">Kalkulacja</button>
        <button v-if="!isInternal" type="button" :class="{ active: priceMode === 'od_masy' }" @click="priceMode = 'od_masy'">Od masy</button>
        <button type="button" :class="{ active: priceMode === 'manual' }" @click="priceMode = 'manual'">Ręczna</button>
      </div>
    </header>

    <div v-if="isLoading" class="loading-state">Ładowanie wyceny...</div>

    <div v-else class="quote-layout">
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

      <template v-if="priceMode !== 'manual'">
        <section v-if="priceMode === 'kalkulacja'" class="quote-block">
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
              <span>Godz.</span>
              <span>PLN/h</span>
              <span>Akcje</span>
            </div>
            <div v-for="(operation, index) in processes" :key="index" class="ops-row">
              <input v-model="operation.name" placeholder="np. Cięcie, spawanie">
              <input v-model="operation.department" list="quote-departments" placeholder="wydział">
              <input type="number" v-model.number="operation.hours" min="0" step="0.25">
              <input type="number" v-model.number="operation.rate_per_hour" min="0" step="5">
              <button type="button" :aria-label="'Usuń operację: ' + (operation.name || ('#' + (index + 1)))" @click="removeOperation(index)">Usuń</button>
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
              <span>Akcje</span>
            </div>
            <div v-for="(line, index) in materials" :key="index" class="mat-row">
              <input v-model="line.name" list="quote-materials" placeholder="np. S235, Hardox">
              <input type="number" v-model.number="line.qty_kg" min="0" step="0.5">
              <input type="number" v-model.number="line.price_per_kg" min="0" step="0.1">
              <div class="line-cost-cell">
                <input v-if="line.cost_override" type="number" v-model.number="line.cost" min="0" step="10" placeholder="koszt">
                <strong v-else>{{ moneyLabel(Number(line.qty_kg || 0) * Number(line.price_per_kg || 0)) }}</strong>
                <button type="button" class="mini-link" @click="line.cost_override = !line.cost_override">
                  {{ line.cost_override ? 'auto' : 'override' }}
                </button>
              </div>
              <button type="button" :aria-label="'Usuń materiał: ' + (line.name || ('#' + (index + 1)))" @click="removeMaterial(index)">Usuń</button>
            </div>
            <p v-if="!materials.length" class="mat-empty">Brak materiałów — dodaj pozycję.</p>
          </div>
          <div
            v-if="materialBruttoKg > 0 && weight_netto_kg > 0"
            class="mass-balance"
            aria-label="Bilans masy"
          >
            <span class="mass-balance-label">Bilans masy</span>
            <span>Materiał (brutto) <strong>{{ materialBruttoKg.toFixed(2) }} kg</strong></span>
            <span>Netto <strong>{{ Number(weight_netto_kg).toFixed(2) }} kg</strong></span>
            <span>Odpad <strong>{{ odpadKg.toFixed(2) }} kg ({{ Math.round(odpadPct * 100) }}%)</strong></span>
          </div>
        </section>

        <section v-if="priceMode === 'od_masy'" class="quote-block">
          <div class="block-head">
            <h3>Wycena od masy</h3>
            <span class="block-hint">Masa × stawka PLN/kg. To osobny sposób liczenia, nie dodatek do materiałów.</span>
          </div>
          <div class="mode-switch mode-switch--inline">
            <button type="button" :class="{ active: weightBasis === 'netto' }" @click="weightBasis = 'netto'">Netto</button>
            <button type="button" :class="{ active: weightBasis === 'brutto' }" @click="weightBasis = 'brutto'">Brutto</button>
          </div>
          <div class="input-grid">
            <label v-if="weightBasis === 'netto'">
              <span>Masa netto do wyceny (kg)</span>
              <input type="number" v-model.number="weight_netto_kg" min="0" step="0.1">
            </label>
            <label v-else>
              <span>Masa brutto do wyceny (kg)</span>
              <input type="number" v-model.number="weight_brutto_kg" min="0" step="0.1" :readonly="weightFromMaterial">
              <small v-if="weightFromMaterial" class="pct-hint">liczone automatycznie z materiałów</small>
            </label>
            <label>
              <span>Stawka PLN/kg</span>
              <input type="number" v-model.number="weight_rate_pln_kg" min="0" step="0.01">
            </label>
          </div>
          <label v-if="weightBasis === 'brutto'" class="checkbox-line" style="margin-top:10px">
            <input type="checkbox" v-model="weightFromMaterial">
            <span>Masa brutto z materiałów (Σ kg)</span>
          </label>
        </section>

        <section v-if="priceMode === 'kalkulacja'" class="quote-block">
          <div class="block-head">
            <h3>{{ isInternal ? 'Robocizna' : 'Robocizna i narzuty' }}</h3>
          </div>
          <div class="input-grid">
            <label>
              <span>Robocizna dodatkowa h</span>
              <input type="number" v-model.number="labor_hours" min="0" step="0.25">
            </label>
            <!-- Internal: marża/overhead/transport/oferta są opcjonalne i schowane.
                 Klient zewnętrzny: zawsze widoczne. -->
            <template v-if="!isInternal || showMarkup">
              <label>
                <span>Overhead (%)</span>
                <input type="number" v-model.number="overhead_pct" min="0" max="100" step="1">
                <small class="pct-hint">narzut w procentach, np. 10</small>
              </label>
              <label>
                <span>Marża (%)</span>
                <input type="number" v-model.number="margin_pct" min="0" max="200" step="1">
                <small class="pct-hint">marża w procentach, np. 25</small>
              </label>
              <label>
                <span>Transport PLN</span>
                <input type="number" v-model.number="transport_cost" min="0" step="50">
              </label>
              <label v-if="!isInternal" class="checkbox-line">
                <input type="checkbox" v-model="show_unit_prices">
                <span>Pokaż ceny operacji na ofercie</span>
              </label>
            </template>
          </div>
          <button v-if="isInternal" type="button" class="markup-toggle" @click="showMarkup = !showMarkup">
            {{ showMarkup ? '▲ Ukryj marżę / narzut' : '▼ Dolicz marżę / narzut (opcjonalnie)' }}
          </button>
        </section>
      </template>

      <section v-else class="quote-block manual-block">
        <h3>Cena ręczna</h3>
        <label>
          <span>Cena netto PLN</span>
          <input type="number" v-model.number="manual_total_net" min="0" step="0.01">
        </label>
      </section>

      <!-- Spacer so the sticky bar doesn't overlap last section -->
      <div class="summary-spacer" aria-hidden="true"></div>
    </div>

    <!-- Sticky bottom summary/save bar -->
    <footer class="quote-action-bar">
      <div class="action-bar-inner">
        <div class="action-bar-breakdown">
          <div class="summary-row" v-if="priceMode === 'kalkulacja'">
            <span>Operacje</span>
            <strong>{{ moneyLabel(operationsTotal) }}</strong>
          </div>
          <div class="summary-row" v-if="priceMode !== 'manual'">
            <span>Materiał</span>
            <strong>{{ moneyLabel(materialTotal) }}</strong>
          </div>
          <div class="summary-row" v-if="priceMode === 'kalkulacja'">
            <span>Rob. dod.</span>
            <strong>{{ moneyLabel(extraLaborTotal) }}</strong>
          </div>
          <div v-if="priceMode === 'od_masy'" class="summary-row">
            <span>Masa {{ weightBasis }}</span>
            <strong>{{ moneyLabel(previewResult?.weight_total ?? pricingWeight * Number(weight_rate_pln_kg || 0)) }}</strong>
          </div>
          <div class="summary-row" v-if="priceMode !== 'manual' && (!isInternal || showMarkup)">
            <span>Transport</span>
            <strong>{{ moneyLabel(transport_cost) }}</strong>
          </div>
          <div class="summary-row summary-row--divider">
            <span>Netto</span>
            <strong>{{ moneyLabel(previewTotal) }}</strong>
          </div>
          <template v-if="!isInternal">
            <div class="summary-row">
              <span>VAT 23%</span>
              <strong>{{ moneyLabel(previewTotal * 0.23) }}</strong>
            </div>
            <div class="summary-row">
              <span>Brutto</span>
              <strong>{{ moneyLabel(previewTotal * 1.23) }}</strong>
            </div>
          </template>
          <div v-if="quantity > 1" class="summary-row">
            <span>Netto / szt.</span>
            <strong>{{ moneyLabel(previewTotal / quantity) }}</strong>
          </div>
        </div>

        <!-- Po zapisie: krok "wydrukuj arkusz" (to jest realny następny krok). -->
        <div v-if="justSaved" class="action-bar-total save-done">
          <span class="save-done-label">Zapisano ✓</span>
          <button class="btn btn-primary" type="button" @click="printArkusz">🖨 Drukuj arkusz</button>
          <button class="close-link" type="button" @click="emit('saved')">Wróć do listy</button>
        </div>
        <div v-else class="action-bar-total">
          <p v-if="previewError" class="preview-error">Podgląd niedostępny — lokalny szacunek.</p>
          <p v-if="saveError" class="preview-error">Nie udało się zapisać. Spróbuj ponownie.</p>
          <div class="total-line">
            <span class="total-label">Netto</span>
            <strong class="total-amount">{{ moneyLabel(previewTotal) }}</strong>
          </div>
          <button class="btn btn-success" type="button" :disabled="isSaving || !canSave" @click="saveQuote">
            {{ isSaving ? 'Zapisywanie...' : 'Zapisz wycenę' }}
          </button>
        </div>
      </div>
    </footer>
  </section>
</template>

<style scoped>
/* ── Panel shell ──
   Panel jest własnym kontenerem scrolla o stałej wysokości: nagłówek i dolny
   pasek to nieruchome ramki (flex 0 0 auto), a przewija się tylko środek
   (.quote-layout). Dzięki temu nagłówek "Wycena/klient" nigdy nie najeżdża na
   treść ani globalną nawigację — bo scroll jest wewnątrz panelu, nie strony. */
.quote-panel {
  background: var(--card);
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.quote-header {
  flex: 0 0 auto;
  z-index: 10;
  border-bottom: 1px solid var(--border);
  background: var(--card);
  padding: 14px 24px;
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

.header-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.close-link {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card);
  color: var(--sl-700);
  padding: 5px 10px;
  cursor: pointer;
  font-size: 0.76rem;
  font-weight: 800;
}
.close-link:hover { border-color: var(--rcm-blue); color: var(--rcm-blue); }

.internal-tag {
  display: inline-block;
  margin-left: 6px;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--rcm-blue);
  color: #fff;
  font-size: 0.68rem;
  font-weight: 850;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  vertical-align: middle;
}

.markup-toggle {
  margin-top: 12px;
  background: none;
  border: none;
  color: var(--rcm-accent);
  font-size: 0.8rem;
  font-weight: 800;
  cursor: pointer;
  padding: 4px 0;
}
.markup-toggle:hover { text-decoration: underline; }

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
  padding: 52px 24px;
  color: var(--muted);
  text-align: center;
}

/* ── Single-column layout — jedyny element przewijany ── */
.quote-layout {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 20px 24px;
}

/* Spacer już niepotrzebny — dolny pasek jest poza obszarem scrolla. */
.summary-spacer { display: none; }

/* ── Order context strip ── */
.order-context {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 10px;
  padding: 13px 16px;
  background: var(--sl-50);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.order-context p {
  grid-column: 1 / -1;
  color: var(--sl-700);
  font-size: 0.86rem;
  line-height: 1.45;
  margin: 0;
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

/* ── Pricing blocks ── */
.quote-block {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--card);
  padding: 16px 18px;
}

.block-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: center;
  margin-bottom: 14px;
}

.quote-block h3 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 0.95rem;
  font-weight: 850;
}

.block-hint {
  color: var(--muted);
  font-size: 0.74rem;
  font-weight: 500;
  text-align: right;
}

.block-head button,
.suggestions button {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card);
  color: var(--sl-700);
  padding: 5px 9px;
  cursor: pointer;
  font-size: 0.74rem;
  font-weight: 850;
}

/* ── Operations table — relaxed full-width columns ── */
.ops-table {
  display: grid;
  gap: 8px;
}

.ops-head,
.ops-row {
  display: grid;
  grid-template-columns: minmax(220px, 2fr) minmax(130px, 1fr) 110px 120px 72px;
  gap: 10px;
  align-items: center;
}

.ops-head {
  color: var(--muted);
  font-size: 0.68rem;
  font-weight: 850;
  text-transform: uppercase;
}

/* ── Materials table — relaxed full-width columns ── */
.mat-table {
  display: grid;
  gap: 8px;
}

.mat-head,
.mat-row {
  display: grid;
  grid-template-columns: minmax(200px, 2fr) 110px 120px 130px 72px;
  gap: 10px;
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

/* ── Shared input styles ── */
.ops-row input,
.mat-row input,
.input-grid input,
.manual-block input {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--card);
  color: var(--text);
  padding: 8px 10px;
  font: inherit;
  font-size: 0.85rem;
  box-sizing: border-box;
}

.ops-row button,
.mat-row button {
  border: 1px solid var(--rcm-red);
  border-radius: 7px;
  background: var(--card);
  color: var(--rcm-red);
  padding: 7px 8px;
  cursor: pointer;
  font-size: 0.72rem;
  font-weight: 850;
}

/* ── Mass balance readout ── */
.mass-balance {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 20px;
  margin-top: 14px;
  padding: 9px 12px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--sl-50);
  font-size: 0.82rem;
  color: var(--muted);
}

.mass-balance-label {
  font-weight: 850;
  text-transform: uppercase;
  font-size: 0.7rem;
  color: var(--muted);
  letter-spacing: 0.04em;
}

.mass-balance strong {
  color: var(--text);
}

/* ── Suggestions ── */
.suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 12px;
}

/* ── Weight / overhead input grids ── */
.input-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 14px;
}

.input-grid label,
.manual-block label {
  display: grid;
  gap: 5px;
}

.pct-hint {
  color: var(--muted);
  font-size: 0.72rem;
}

/* ── Weight advanced collapsible ── */
.weight-advanced {
  margin-top: 12px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--sl-50);
}

.weight-advanced summary {
  cursor: pointer;
  padding: 7px 12px;
  font-size: 0.78rem;
  font-weight: 850;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  list-style: none;
  user-select: none;
}

.weight-advanced summary::-webkit-details-marker {
  display: none;
}

.weight-advanced summary::before {
  content: '+ ';
}

.weight-advanced[open] summary::before {
  content: '- ';
}

.weight-advanced-grid {
  padding: 10px 12px 12px;
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

/* ── Manual price block ── */
.manual-block {
  display: grid;
  gap: 12px;
}

.manual-block input {
  max-width: 260px;
  border-width: 2px;
  border-color: var(--rcm-green);
  font-size: 1.3rem;
  font-weight: 850;
}

/* ── Dolny pasek akcji — nieruchoma rama panelu ── */
.quote-action-bar {
  flex: 0 0 auto;
  z-index: 10;
  border-top: 1px solid var(--border);
  background: var(--card);
  box-shadow: 0 -2px 12px rgba(0, 0, 0, 0.07);
}

.action-bar-inner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  padding: 12px 24px;
  flex-wrap: wrap;
}

.action-bar-breakdown {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 24px;
  flex: 1;
  min-width: 0;
}

.action-bar-total {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-shrink: 0;
}

.save-done-label {
  color: var(--green, #166534);
  font-weight: 850;
  font-size: 0.95rem;
}

.summary-row {
  display: flex;
  flex-direction: column;
  gap: 1px;
  color: var(--muted);
  font-size: 0.78rem;
  white-space: nowrap;
}

.summary-row strong {
  color: var(--text);
  font-size: 0.84rem;
}

/* Separator before the Netto→VAT→Brutto split */
.summary-row--divider {
  border-left: 2px solid var(--border);
  padding-left: 12px;
  margin-left: 4px;
}

.total-line {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 1px;
}

.total-label {
  color: var(--muted);
  font-size: 0.72rem;
  font-weight: 850;
  text-transform: uppercase;
}

.total-amount {
  color: var(--rcm-blue);
  font-family: var(--font-data);
  font-size: 1.55rem;
  font-weight: 850;
  line-height: 1;
}

.preview-error {
  border-radius: 7px;
  background: rgba(220, 38, 38, 0.09);
  color: var(--rcm-red);
  padding: 7px 10px;
  font-size: 0.76rem;
  font-weight: 750;
  max-width: 240px;
}

/* ── Responsive ── */
@media (max-width: 860px) {
  .ops-head,
  .mat-head {
    display: none;
  }

  .ops-row,
  .mat-row {
    grid-template-columns: 1fr 1fr;
    row-gap: 6px;
  }

  .ops-row button,
  .mat-row button {
    grid-column: 1 / -1;
  }

  .input-grid {
    grid-template-columns: 1fr 1fr;
  }

  .order-context {
    grid-template-columns: 1fr 1fr;
  }

  .action-bar-inner {
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
  }

  .action-bar-total {
    width: 100%;
    justify-content: space-between;
  }
}
</style>
