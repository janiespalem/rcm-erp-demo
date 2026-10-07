<script setup>
import { computed, reactive, ref } from 'vue'

import { calculateTetrapodPlan, createLocalId, sumSteelDeliveries } from '@/utils/tetrapodCalculator'

const STEEL_DIAMETERS = [6, 12, 16]
const DELIVERY_STORAGE_KEY = 'factoryflow-demo-tetrapod-steel-deliveries-v1'
const planned = ref(0)
const completed = ref(0)
const emptyDelivery = () => ({ 6: null, 12: null, 16: null })
const deliveryDraft = reactive(emptyDelivery())
const deliveryError = ref('')

function loadDeliveries() {
  try {
    const stored = JSON.parse(localStorage.getItem(DELIVERY_STORAGE_KEY) || '[]')
    if (!Array.isArray(stored)) return []
    return stored.filter(delivery => {
      const weights = STEEL_DIAMETERS.map(diameter => delivery?.[`kg${diameter}`])
      return typeof delivery?.id === 'string'
        && Number.isFinite(Date.parse(delivery.receivedAt))
        && weights.every(weight => Number.isFinite(weight) && weight >= 0)
        && weights.some(weight => weight > 0)
    })
  } catch {
    return []
  }
}

const steelDeliveries = ref(loadDeliveries())
const deliveryTotals = computed(() => sumSteelDeliveries(steelDeliveries.value))

const calculation = computed(() => {
  try {
    return {
      result: calculateTetrapodPlan(planned.value, completed.value, deliveryTotals.value),
      error: '',
    }
  } catch (error) {
    return {
      result: null,
      error: error instanceof RangeError
        ? error.message
        : 'Nie udało się wykonać obliczeń.',
    }
  }
})

const integer = new Intl.NumberFormat('pl-PL', { maximumFractionDigits: 0 })
const kilograms = new Intl.NumberFormat('pl-PL', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
const metres = new Intl.NumberFormat('pl-PL', { minimumFractionDigits: 0, maximumFractionDigits: 2 })
const dates = new Intl.DateTimeFormat('pl-PL', { day: '2-digit', month: '2-digit', year: 'numeric' })
const times = new Intl.DateTimeFormat('pl-PL', { hour: '2-digit', minute: '2-digit' })
const weekdays = new Intl.DateTimeFormat('pl-PL', { weekday: 'long' })

const formatInteger = value => integer.format(value)
const formatKg = value => `${kilograms.format(value)} kg`
const formatMetres = value => `${metres.format(value)} m`
const formatDeliveryDate = value => dates.format(new Date(value))
const formatDeliveryTime = value => times.format(new Date(value))
const formatDeliveryWeekday = value => {
  const day = weekdays.format(new Date(value))
  return day.charAt(0).toUpperCase() + day.slice(1)
}
const balanceLabel = row => row.balanceKg >= 0
  ? `Zostanie ${formatKg(row.balanceKg)}`
  : `Brakuje ${formatKg(Math.abs(row.balanceKg))}`
const formatLength = part => part.dimensions
  ? `${part.dimensions} (${integer.format(part.lengthMm)} mm)`
  : `${integer.format(part.lengthMm)} mm`

function addSteelDelivery() {
  deliveryError.value = ''
  const weights = Object.fromEntries(
    STEEL_DIAMETERS.map(diameter => [diameter, deliveryDraft[diameter] === '' || deliveryDraft[diameter] === null
      ? 0
      : Number(deliveryDraft[diameter])]),
  )
  if (Object.values(weights).some(weight => !Number.isFinite(weight) || weight < 0)
    || !Object.values(weights).some(weight => weight > 0)) {
    deliveryError.value = 'Wpisz dodatnią masę dla co najmniej jednej średnicy.'
    return
  }

  const entry = {
    id: createLocalId(),
    receivedAt: new Date().toISOString(),
    kg6: weights[6],
    kg12: weights[12],
    kg16: weights[16],
  }
  const next = [entry, ...steelDeliveries.value]
  try {
    localStorage.setItem(DELIVERY_STORAGE_KEY, JSON.stringify(next))
  } catch {
    deliveryError.value = 'Nie udało się zapisać dostawy w tej przeglądarce.'
    return
  }
  steelDeliveries.value = next
  Object.assign(deliveryDraft, emptyDelivery())
}

function removeSteelDelivery(id) {
  if (!window.confirm('Usunąć tę dostawę z historii?')) return
  const next = steelDeliveries.value.filter(delivery => delivery.id !== id)
  try {
    localStorage.setItem(DELIVERY_STORAGE_KEY, JSON.stringify(next))
  } catch {
    deliveryError.value = 'Nie udało się usunąć dostawy w tej przeglądarce.'
    return
  }
  steelDeliveries.value = next
}
</script>

<template>
  <section class="tetrapod-calculator">
    <header class="production-header">
      <div>
        <p class="eyebrow">Norma produkcyjna · zbrojenie</p>
        <h1>Tetrapod</h1>
        <p class="subtitle">4 kosze · 44 elementy · 56,092 kg stali na sztukę</p>
      </div>
      <div class="norm-badge" aria-label="Masa jednego kosza">
        <span>1 kosz</span>
        <strong>14,023 kg</strong>
      </div>
    </header>

    <div class="input-panel">
      <div class="input-field">
        <label for="tetrapod-planned">Planowana liczba tetrapodów</label>
        <input
          id="tetrapod-planned"
          v-model.number="planned"
          type="number"
          min="0"
          step="1"
          inputmode="numeric"
        >
      </div>
      <div class="input-field">
        <label for="tetrapod-completed">Już wykonano</label>
        <input
          id="tetrapod-completed"
          v-model.number="completed"
          type="number"
          min="0"
          :max="Number.isInteger(planned) ? planned : undefined"
          step="1"
          inputmode="numeric"
        >
      </div>
      <p v-if="calculation.error" class="input-error" role="alert">
        {{ calculation.error }}
      </p>
    </div>

    <form class="delivery-panel" @submit.prevent="addSteelDelivery">
      <div class="delivery-heading">
        <span>Dostawa stali</span>
        <small>Wpisz jedną dostawę i dodaj ją do historii. Data, godzina i dzień tygodnia zapiszą się automatycznie.</small>
      </div>
      <div v-for="diameter in STEEL_DIAMETERS" :key="diameter" class="input-field delivery-field">
        <label :for="`delivery-${diameter}`">Ø{{ diameter }}</label>
        <div class="input-with-unit">
          <input
            :id="`delivery-${diameter}`"
            v-model.number="deliveryDraft[diameter]"
            type="number"
            min="0"
            step="0.001"
            inputmode="decimal"
            placeholder="0"
            @input="deliveryError = ''"
          >
          <span>kg</span>
        </div>
      </div>
      <button class="add-delivery" type="submit">Dodaj dostawę</button>
      <p v-if="deliveryError" class="delivery-error" role="alert">{{ deliveryError }}</p>
    </form>

    <article class="delivery-history">
      <div class="history-heading">
        <div>
          <h2>Historia dostaw</h2>
          <p>Każda przyjęta dostawa jest osobnym wpisem. Najnowsze są na górze.</p>
        </div>
        <strong>Liczba wpisów: {{ steelDeliveries.length }}</strong>
      </div>
      <div v-if="steelDeliveries.length" class="table-scroll">
        <table class="delivery-table">
          <thead>
            <tr>
              <th>Data</th>
              <th>Dzień tygodnia</th>
              <th>Godzina</th>
              <th class="number">Ø6</th>
              <th class="number">Ø12</th>
              <th class="number">Ø16</th>
              <th>Akcja</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="delivery in steelDeliveries" :key="delivery.id">
              <td>{{ formatDeliveryDate(delivery.receivedAt) }}</td>
              <td>{{ formatDeliveryWeekday(delivery.receivedAt) }}</td>
              <td>{{ formatDeliveryTime(delivery.receivedAt) }}</td>
              <td class="number">{{ formatKg(delivery.kg6) }}</td>
              <td class="number">{{ formatKg(delivery.kg12) }}</td>
              <td class="number">{{ formatKg(delivery.kg16) }}</td>
              <td class="history-action">
                <button type="button" @click="removeSteelDelivery(delivery.id)">Usuń</button>
              </td>
            </tr>
          </tbody>
          <tfoot>
            <tr>
              <td colspan="3">Dostarczono łącznie</td>
              <td v-for="diameter in STEEL_DIAMETERS" :key="diameter" class="number">
                {{ formatKg(deliveryTotals[diameter] || 0) }}
              </td>
              <td></td>
            </tr>
          </tfoot>
        </table>
      </div>
      <p v-else class="history-empty">Brak zapisanych dostaw. Wpisz pierwszą dostawę powyżej.</p>
    </article>

    <template v-if="calculation.result">
      <div class="summary-grid" aria-live="polite">
        <article class="summary-card">
          <span>Plan</span>
          <strong>{{ formatInteger(calculation.result.tetrapods.planned) }}</strong>
          <small>tetrapodów</small>
        </article>
        <article class="summary-card completed">
          <span>Wykonano</span>
          <strong>{{ formatInteger(calculation.result.tetrapods.completed) }}</strong>
          <small>tetrapodów</small>
        </article>
        <article class="summary-card remaining">
          <span>Pozostało</span>
          <strong>{{ formatInteger(calculation.result.tetrapods.remaining) }}</strong>
          <small>tetrapodów</small>
        </article>
        <article class="summary-card">
          <span>Kosze do wykonania</span>
          <strong>{{ formatInteger(calculation.result.baskets.remaining) }}</strong>
          <small>sztuk</small>
        </article>
        <article class="summary-card steel">
          <span>Stal do przygotowania</span>
          <strong>{{ formatKg(calculation.result.weightsKg.remaining) }}</strong>
          <small>pozostała masa</small>
        </article>
      </div>

      <article class="sheet-card">
        <div class="section-heading">
          <div>
            <p class="section-index">01</p>
            <h2>Norma elementów</h2>
          </div>
          <p>Stałe wartości dla jednego kosza i kompletnego tetrapodu.</p>
        </div>
        <div class="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Element</th>
                <th>Średnica</th>
                <th>Długość</th>
                <th class="number">Szt. / kosz</th>
                <th class="number">Kg / kosz</th>
                <th class="number">Szt. / tetrapod</th>
                <th class="number">Kg / tetrapod</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="part in calculation.result.parts" :key="part.key">
                <td class="part-name">{{ part.name }}</td>
                <td class="diameter">Ø{{ part.diameterMm }}</td>
                <td>{{ formatLength(part) }}</td>
                <td class="number">{{ formatInteger(part.quantities.perBasket) }}</td>
                <td class="number">{{ formatKg(part.weightsKg.perBasket) }}</td>
                <td class="number">{{ formatInteger(part.quantities.perTetrapod) }}</td>
                <td class="number strong">{{ formatKg(part.weightsKg.perTetrapod) }}</td>
              </tr>
            </tbody>
            <tfoot>
              <tr>
                <td colspan="3">Razem</td>
                <td class="number">11</td>
                <td class="number">{{ formatKg(calculation.result.weightsKg.perBasket) }}</td>
                <td class="number">44</td>
                <td class="number">{{ formatKg(calculation.result.weightsKg.perTetrapod) }}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      </article>

      <article class="sheet-card">
        <div class="section-heading">
          <div>
            <p class="section-index">02</p>
            <h2>Realizacja planu</h2>
          </div>
          <p>Liczba elementów i masa dla całego zlecenia.</p>
        </div>
        <div class="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Element</th>
                <th class="number">Plan</th>
                <th class="number">Wykonano</th>
                <th class="number accent-column">Pozostało</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="part in calculation.result.parts" :key="part.key">
                <td>
                  <span class="part-name">{{ part.name }}</span>
                  <small>Ø{{ part.diameterMm }} · {{ formatLength(part) }}</small>
                </td>
                <td class="number split-value">
                  <strong>{{ formatInteger(part.quantities.planned) }} szt.</strong>
                  <span>{{ formatKg(part.weightsKg.planned) }}</span>
                </td>
                <td class="number split-value">
                  <strong>{{ formatInteger(part.quantities.completed) }} szt.</strong>
                  <span>{{ formatKg(part.weightsKg.completed) }}</span>
                </td>
                <td class="number split-value accent-column">
                  <strong>{{ formatInteger(part.quantities.remaining) }} szt.</strong>
                  <span>{{ formatKg(part.weightsKg.remaining) }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </article>

      <article class="sheet-card">
        <div class="section-heading">
          <div>
            <p class="section-index">03</p>
            <h2>Zbrojenie i bilans dostawy</h2>
          </div>
          <p>Gotowa lista materiału, który pozostał do przygotowania.</p>
        </div>
        <div class="diameter-grid">
          <div
            v-for="row in calculation.result.diameters"
            :key="row.diameterMm"
            class="diameter-card"
          >
            <strong>Ø{{ row.diameterMm }}</strong>
            <dl>
              <div><dt>Elementy</dt><dd>{{ formatInteger(row.remaining.quantity) }} szt.</dd></div>
              <div><dt>Długość</dt><dd>{{ formatMetres(row.remaining.lengthM) }}</dd></div>
              <div><dt>Potrzeba na resztę</dt><dd>{{ formatKg(row.remaining.weightKg) }}</dd></div>
              <template v-if="row.deliveryKg !== null">
                <div><dt>Dostarczono</dt><dd>{{ formatKg(row.deliveryKg) }}</dd></div>
                <div><dt>Zużyto teoretycznie</dt><dd>{{ formatKg(row.consumedKg) }}</dd></div>
                <div><dt>Stan teraz</dt><dd>{{ formatKg(row.availableKg) }}</dd></div>
              </template>
            </dl>
            <div
              v-if="row.deliveryKg !== null"
              class="delivery-balance"
              :class="row.balanceKg >= 0 ? 'surplus' : 'shortage'"
            >
              <span>Bilans po całym planie</span>
              <strong>{{ balanceLabel(row) }}</strong>
            </div>
            <div v-else class="delivery-balance empty">
              <span>Dostawy</span>
              <strong>Brak zapisanych dostaw</strong>
            </div>
          </div>
        </div>
      </article>
    </template>
  </section>
</template>

<style scoped>
.tetrapod-calculator {
  display: grid;
  gap: 16px;
  max-width: 1500px;
  margin: 0 auto;
}

.production-header {
  position: relative;
  overflow: hidden;
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 24px;
  padding: 26px 28px;
  border-radius: 10px;
  color: #fff;
  background: var(--sl-900);
  box-shadow: var(--sh-md);
}
.production-header::after {
  content: '';
  position: absolute;
  inset: 0 0 0 auto;
  width: 34%;
  opacity: .13;
  background: repeating-linear-gradient(135deg, transparent 0 16px, var(--amber) 16px 20px);
  pointer-events: none;
}
.production-header > * { position: relative; z-index: 1; }
.eyebrow {
  margin: 0 0 4px;
  color: var(--amber);
  font: 600 .72rem/1.4 var(--font-data);
  letter-spacing: .12em;
  text-transform: uppercase;
}
.production-header h1 { margin: 0; font-size: 2rem; line-height: 1.05; }
.subtitle { margin: 8px 0 0; color: var(--sl-300); font-size: .92rem; }
.norm-badge {
  min-width: 150px;
  padding: 11px 14px;
  border: 1px solid rgba(255,255,255,.18);
  border-radius: 7px;
  background: rgba(255,255,255,.06);
  text-align: right;
}
.norm-badge span { display: block; color: var(--sl-400); font-size: .72rem; text-transform: uppercase; }
.norm-badge strong { font: 700 1.05rem/1.5 var(--font-data); }

.input-panel {
  display: grid;
  grid-template-columns: repeat(2, minmax(210px, 280px)) 1fr;
  align-items: end;
  gap: 14px;
  padding: 18px;
  border: 1px solid var(--border);
  border-left: 4px solid var(--amber);
  border-radius: var(--radius);
  background: var(--card);
  box-shadow: var(--sh-sm);
}
.input-field { display: grid; gap: 6px; }
.input-field input {
  min-height: 48px;
  padding: 8px 13px;
  font-size: 1.15rem;
  font-weight: 700;
}
.input-error { align-self: center; color: var(--red); font-size: .86rem; font-weight: 600; }

.delivery-panel {
  display: grid;
  grid-template-columns: minmax(260px, 1fr) repeat(3, minmax(130px, 180px)) minmax(145px, auto);
  align-items: end;
  gap: 14px;
  padding: 16px 18px;
  border: 1px solid #f4cf77;
  border-left: 4px solid var(--amber);
  border-radius: var(--radius);
  background: var(--amber-soft);
  box-shadow: var(--sh-sm);
}
.delivery-heading { align-self: center; }
.delivery-heading span {
  display: block;
  margin-bottom: 3px;
  font-weight: 750;
  color: var(--sl-900);
}
.delivery-heading small { display: block; max-width: 520px; color: var(--muted); line-height: 1.35; }
.delivery-field label { color: var(--sl-800); font: 700 .9rem var(--font-data); }
.input-with-unit { position: relative; }
.input-with-unit input { width: 100%; padding-right: 42px; background: var(--card); }
.input-with-unit span {
  position: absolute;
  right: 13px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--muted);
  font: 600 .78rem var(--font-data);
  pointer-events: none;
}
.add-delivery {
  min-height: 48px;
  padding: 10px 18px;
  border: 1px solid var(--amber-dark);
  border-radius: 7px;
  color: #fff;
  background: var(--amber-dark);
  font-weight: 750;
  white-space: nowrap;
  cursor: pointer;
}
.add-delivery:hover { filter: brightness(.94); }
.add-delivery:focus-visible { outline: 3px solid rgba(217, 119, 6, .28); outline-offset: 2px; }
.delivery-error { grid-column: 1 / -1; margin: -2px 0 0; color: var(--red); font-size: .82rem; font-weight: 650; }

.delivery-history {
  padding: 18px 20px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--card);
  box-shadow: var(--sh-sm);
}
.history-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 18px;
  margin-bottom: 12px;
}
.history-heading h2 { margin: 0; font-size: 1.05rem; }
.history-heading p { margin: 4px 0 0; color: var(--muted); font-size: .8rem; }
.history-heading > strong {
  flex: none;
  padding: 5px 9px;
  border-radius: 999px;
  color: var(--sl-700);
  background: var(--sl-100);
  font: 700 .76rem var(--font-data);
}
.delivery-table { min-width: 780px; }
.delivery-table td { font-size: .82rem; }
.delivery-table td:nth-child(n+4) { font-family: var(--font-data); }
.history-action { text-align: right; }
.history-action button {
  padding: 5px 8px;
  border: 1px solid #fecaca;
  border-radius: 5px;
  color: var(--red);
  background: #fff7f7;
  font-size: .72rem;
  font-weight: 700;
  cursor: pointer;
}
.history-empty {
  margin: 0;
  padding: 18px;
  border: 1px dashed var(--sl-300);
  border-radius: 7px;
  color: var(--muted);
  background: var(--sl-50);
  font-size: .84rem;
  text-align: center;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 10px;
}
.summary-card {
  min-width: 0;
  padding: 15px 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--card);
  box-shadow: var(--sh-xs);
}
.summary-card span, .summary-card small { display: block; color: var(--muted); }
.summary-card span { margin-bottom: 4px; font-size: .71rem; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.summary-card strong { display: block; overflow: hidden; font: 700 1.55rem/1.3 var(--font-data); text-overflow: ellipsis; }
.summary-card small { font-size: .72rem; }
.summary-card.completed { border-top: 3px solid var(--green); }
.summary-card.remaining, .summary-card.steel { border-top: 3px solid var(--amber); }
.summary-card.steel { background: var(--amber-soft); border-color: #f8d88d; }

.sheet-card {
  padding: 20px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--card);
  box-shadow: var(--sh-sm);
}
.section-heading {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 14px;
}
.section-heading > div { display: flex; align-items: baseline; gap: 10px; }
.section-index { color: var(--amber-dark); font: 700 .74rem/1 var(--font-data); }
.section-heading h2 { margin: 0; font-size: 1.05rem; }
.section-heading > p { color: var(--muted); font-size: .8rem; text-align: right; }
.table-scroll { overflow-x: auto; }
table { min-width: 860px; }
th { white-space: nowrap; }
td { vertical-align: middle; }
.number { text-align: right; font-family: var(--font-data); font-variant-numeric: tabular-nums; }
.part-name { font-weight: 650; color: var(--text); }
.diameter { font: 700 .86rem var(--font-data); }
.strong { font-weight: 700; }
.split-value strong, .split-value span { display: block; white-space: nowrap; }
.split-value span { margin-top: 2px; color: var(--muted); font-size: .76rem; }
td small { display: block; margin-top: 2px; color: var(--muted); font-size: .73rem; }
.accent-column { background: #fffbeb; }
tfoot td { border-top: 2px solid var(--sl-300); font-weight: 700; }

.diameter-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; }
.diameter-card {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 20px;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--sl-50);
}
.diameter-card > strong { color: var(--sl-900); font: 700 1.4rem var(--font-data); }
.diameter-card dl { display: grid; gap: 5px; }
.diameter-card dl div { display: flex; justify-content: space-between; gap: 12px; }
.diameter-card dt { color: var(--muted); font-size: .78rem; }
.diameter-card dd { font: 600 .82rem var(--font-data); }
.delivery-balance {
  grid-column: 1 / -1;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: -5px -6px -6px;
  padding: 10px 12px;
  border-radius: 5px;
  background: var(--card);
  font-size: .76rem;
}
.delivery-balance strong { font: 700 .86rem var(--font-data); }
.delivery-balance.surplus { color: var(--green); background: #ecfdf3; }
.delivery-balance.shortage { color: var(--red); background: #fff1f2; }
.delivery-balance.empty { color: var(--muted); }

@media (max-width: 1100px) {
  .delivery-panel { grid-template-columns: repeat(3, 1fr) minmax(145px, auto); }
  .delivery-heading { grid-column: 1 / -1; }
  .summary-grid { grid-template-columns: repeat(3, 1fr); }
  .diameter-grid { grid-template-columns: 1fr; }
}
@media (max-width: 700px) {
  .production-header { align-items: flex-start; padding: 20px; }
  .norm-badge { display: none; }
  .input-panel { grid-template-columns: 1fr; }
  .delivery-panel { grid-template-columns: 1fr; }
  .delivery-heading { grid-column: auto; }
  .history-heading { display: block; }
  .history-heading > strong { display: inline-block; margin-top: 9px; }
  .summary-grid { grid-template-columns: repeat(2, 1fr); }
  .summary-card.steel { grid-column: 1 / -1; }
  .section-heading { display: block; }
  .section-heading > p { margin-top: 5px; text-align: left; }
}
</style>
