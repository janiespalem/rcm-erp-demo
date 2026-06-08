<script setup>
import { ref, computed } from 'vue'
import { api } from '@/composables/useApi'
import { useOrders } from '@/composables/useOrders'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { defaultDeadline } from '@/utils/format'

const emit = defineEmits(['switch-tab'])

const { loadOrders, uniqueClients } = useOrders()
const { approvedMaterials } = useApprovedMaterials()

// NOTE (multiple materials): the order-create payload (OrderCreate) and Order.material
// store a SINGLE material string. True multi-material orders need a backend/domain slice
// (extend OrderCreate with a materials list → persist to the existing MaterialRequest
// table). Until then this form intentionally exposes one material field only.
const EMPTY_FORM = () => ({
  client: '',
  deadline: defaultDeadline(),
  description: '',
  quantity: 1,
  material: '',
  // advanced / optional
  order_number: '',
  notes: '',
  estimated_value: 0,
  has_drawing: false,
  requires_visit: false,
  is_defence: false,
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
    title: 'Zlecenie utworzone i przyjęte do produkcji',
    sub:   'Wycena policzona automatycznie. Zlecenie jest już na liście.',
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

async function submit() {
  if (!canSubmit.value || saving.value) return
  error.value = null
  saving.value = true
  try {
    const order = await api('/orders', { method: 'POST', body: form.value })
    const triage = await api(`/orders/${order.id}/triage`, { method: 'POST' })
    result.value = { ...triage, order_number: order.order_number || ('#' + order.id) }
    loadOrders()
  } catch (e) {
    error.value = e.message || 'Nie udało się utworzyć zlecenia — sprawdź połączenie.'
  } finally {
    saving.value = false
  }
}

function goToOrders() {
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
        <div class="result-title">{{ RESULT_INFO[result.branch]?.title || 'Zlecenie utworzone' }}</div>
        <div class="result-sub">
          {{ RESULT_INFO[result.branch]?.sub }}
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
        <button class="btn btn-primary" @click="goToOrders">Przejdź do zleceń →</button>
        <button class="btn btn-outline" @click="resetForm">+ Kolejne zlecenie</button>
      </div>
    </template>

    <!-- FORM -->
    <template v-else>
      <h2>Nowe zlecenie</h2>

      <div class="form-panel">
        <div class="form-grid-2">
          <div class="form-group">
            <label>Klient *</label>
            <input v-model="form.client" list="clients-datalist" placeholder="Nazwa firmy / osoby">
            <datalist id="clients-datalist">
              <option v-for="c in uniqueClients" :key="c" :value="c" />
            </datalist>
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
            <label>Materiał</label>
            <input v-model="form.material" list="materials-datalist" placeholder="np. S355, Hardox, żeliwo">
            <datalist id="materials-datalist">
              <option v-for="m in materialNames" :key="m" :value="m" />
            </datalist>
            <small class="field-hint">Na razie jeden materiał na zlecenie.</small>
          </div>
        </div>
      </div>

      <button class="btn-more-toggle" @click="showMore = !showMore">
        {{ showMore ? '▲ Mniej opcji' : '▼ Więcej opcji' }}
      </button>

      <div v-if="showMore" class="form-panel">
        <div class="form-grid-2">
          <div class="form-group">
            <label>Nr zlecenia</label>
            <input v-model="form.order_number" placeholder="auto albo np. 45/26">
            <small class="field-hint">Puste pole = system nada kolejny numer.</small>
          </div>
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
              <strong>Projekt zbrojeniowy / MON</strong>
              <span>Dokumenty i oferty traktowane jako poufne.</span>
            </span>
          </label>
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
  max-width: 760px;
}

.form-grid-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
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

@media (max-width: 600px) {
  .form-grid-2 { grid-template-columns: 1fr; }
}
</style>
