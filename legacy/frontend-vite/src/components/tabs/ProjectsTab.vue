<script setup>
import { ref, onMounted } from 'vue'
import { RefreshCw } from 'lucide-vue-next'
import { api, openPdf } from '@/composables/useApi'
import { useSettings } from '@/composables/useSettings'
import { useToast } from '@/composables/useToast'

const DEFAULT_MARGIN_PCT = 0.25

const { settings } = useSettings()
const { show } = useToast()

const projectsList         = ref([])
const expandedProj         = ref(null)
const projPositions        = ref({})
const projCatFilter        = ref('')
const expandedPosId        = ref(null)
const posEditOps           = ref({})
const posEditMats          = ref({})
const posSaveMsg           = ref({})
const drawingExtract       = ref({})
const drawingExtractLoading = ref({})

async function load() {
  projectsList.value = await api('/projects')
}

onMounted(load)

async function toggleProjPositions(code) {
  if (expandedProj.value === code) { expandedProj.value = null; return }
  expandedProj.value = code
  if (!projPositions.value[code]) {
    const all = await api(`/templates?project_code=${encodeURIComponent(code)}`)
    projPositions.value = { ...projPositions.value, [code]: all }
    for (const t of all) {
      if (!posEditOps.value[t.id]) {
        posEditOps.value = {
          ...posEditOps.value,
          [t.id]: (t.operations_json || []).map(o => ({
            ...o, op: o.op || o.wydział || o.name || '', hours: o.hours || 0, rate: o.rate_per_hour || settings.value.labor_rate_pln,
          })),
        }
      }
      if (!posEditMats.value[t.id]) {
        posEditMats.value = {
          ...posEditMats.value,
          [t.id]: (t.materials_json || []).map(m => ({
            ...m, mat: m.mat || m.name || '', dim: m.dim || '', qty: m.qty || 1, unit: m.unit || 'szt', mass_kg: m.mass_kg ?? null,
          })),
        }
      }
    }
  }
}

function projCategories(code) {
  return [...new Set((projPositions.value[code] || []).map(p => p.category))].sort()
}

function filteredPositions(code) {
  const sorted = [...(projPositions.value[code] || [])].sort((a, b) =>
    (a.position_nr || a.name).localeCompare(b.position_nr || b.name, 'pl', { numeric: true })
  )
  return projCatFilter.value ? sorted.filter(p => p.category === projCatFilter.value) : sorted
}

function posCalcLabor(posId) {
  return (posEditOps.value[posId] || []).reduce((s, o) => s + (o.hours || 0) * (o.rate || settings.value.labor_rate_pln), 0)
}

async function savePosHours(pos) {
  const ops  = posEditOps.value[pos.id]  || []
  const mats = posEditMats.value[pos.id] || []
  const laborCost = posCalcLabor(pos.id)
  const base = Math.round(laborCost * (1 + (pos.margin_pct || DEFAULT_MARGIN_PCT)))
  const body = {
    operations_json: ops.map(o => ({ ...o, op: o.op, wydział: o.wydział || '', hours: o.hours, rate_per_hour: o.rate })),
    materials_json:  mats.map(m => ({ ...m, mat: m.mat, dim: m.dim, qty: m.qty, unit: m.unit, mass_kg: m.mass_kg ?? null })),
  }
  if (!pos.project_code) body.base_price_pln = base > 0 ? base : pos.base_price_pln
  await api(`/templates/${pos.id}`, { method: 'PATCH', body })
  posSaveMsg.value = { ...posSaveMsg.value, [pos.id]: 'Zapisano' }
  setTimeout(() => { posSaveMsg.value = { ...posSaveMsg.value, [pos.id]: '' } }, 3000)
  const all = await api(`/templates?project_code=${encodeURIComponent(pos.project_code || '')}`)
  projPositions.value = { ...projPositions.value, [pos.project_code]: all }
}

async function uploadPosDrawing(posId, event) {
  const file = event.target.files[0]
  if (!file) return
  const fd = new FormData()
  fd.append('file', file)
  await api(`/templates/${posId}/drawing`, { method: 'POST', body: fd })
  const code = Object.keys(projPositions.value).find(k =>
    (projPositions.value[k] || []).some(p => p.id === posId)
  )
  if (code) {
    projPositions.value = { ...projPositions.value, [code]: await api(`/templates?project_code=${encodeURIComponent(code)}`) }
  }
}

async function extractPosDrawing(pos, apply = false) {
  drawingExtractLoading.value = { ...drawingExtractLoading.value, [pos.id]: true }
  try {
    const result = await api(`/templates/${pos.id}/drawing/extract${apply ? '?apply=true' : ''}`, { method: 'POST' })
    const data = result.extracted
    drawingExtract.value = { ...drawingExtract.value, [pos.id]: data }
    if (apply) {
      const code = pos.project_code || Object.keys(projPositions.value).find(k =>
        (projPositions.value[k] || []).some(p => p.id === pos.id)
      )
      if (code) {
        const all = await api(`/templates?project_code=${encodeURIComponent(code)}`)
        projPositions.value = { ...projPositions.value, [code]: all }
        const updated = all.find(p => p.id === pos.id)
        if (updated) {
          posEditOps.value = { ...posEditOps.value, [pos.id]: (updated.operations_json || []).map(o => ({ ...o, op: o.op || o.name || '', hours: o.hours || 0, rate: o.rate_per_hour || settings.value.labor_rate_pln })) }
          posEditMats.value = { ...posEditMats.value, [pos.id]: (updated.materials_json || []).map(m => ({ ...m, mat: m.mat || m.name || '', dim: m.dim || '', qty: m.qty || 1, unit: m.unit || 'szt', mass_kg: m.mass_kg ?? null })) }
          posSaveMsg.value = { ...posSaveMsg.value, [pos.id]: 'Odczytano PDF i zapisano' }
        }
      }
    }
  } catch (e) {
    show(e.message || 'Nie udało się odczytać PDF')
  } finally {
    drawingExtractLoading.value = { ...drawingExtractLoading.value, [pos.id]: false }
  }
}
</script>

<template>
  <div>
    <div class="tab-toolbar">
      <span></span>
      <button class="btn btn-outline btn-sm" @click="load()">
        <RefreshCw :size="14" /> Odśwież
      </button>
    </div>

    <div v-if="!projectsList.length" class="empty-state">
      Brak projektów w katalogu
    </div>

    <div v-else class="projects-list">
      <div v-for="proj in projectsList" :key="proj.project_code" class="card proj-card">
        <div class="proj-header" @click="toggleProjPositions(proj.project_code)">
          <div>
            <span class="proj-code">{{ proj.project_code }}</span>
            <span class="proj-count">{{ proj.positions_count }} pozycji</span>
          </div>
          <div class="proj-header-actions">
            <button class="btn btn-outline btn-sm" @click.stop="openPdf('/projects/'+proj.project_code+'/arkusze')">
              Drukuj wszystkie
            </button>
            <span class="proj-chevron">{{ expandedProj === proj.project_code ? '▲' : '▼' }}</span>
          </div>
        </div>

        <div v-if="expandedProj === proj.project_code">
          <div v-if="!projPositions[proj.project_code]" class="loading-note">
            Ładowanie...
          </div>
          <div v-else>
            <div class="cat-filter-bar">
              <button v-for="cat in projCategories(proj.project_code)" :key="cat"
                      class="btn btn-sm"
                      :class="projCatFilter === cat ? 'btn-primary' : 'btn-ghost'"
                      @click="projCatFilter = projCatFilter === cat ? '' : cat">
                {{ cat }}
              </button>
            </div>
            <div style="overflow-x:auto">
              <table class="data-table">
                <thead>
                  <tr>
                    <th style="width:100px">Poz.</th>
                    <th>Nazwa / Materiał</th>
                    <th>Kategoria</th>
                    <th class="num">Cena/szt.</th>
                    <th style="text-align:center">Akcje</th>
                  </tr>
                </thead>
                <tbody>
                  <template v-for="pos in filteredPositions(proj.project_code)" :key="pos.id">
                    <tr :class="{'project-row-open': expandedPosId === pos.id}">
                      <td class="pos-nr">{{ pos.position_nr || '—' }}</td>
                      <td>
                        <div>{{ pos.name }}</div>
                        <div v-if="pos.notes" class="pos-notes">{{ pos.notes }}</div>
                      </td>
                      <td>
                        <span class="badge badge-draft" style="font-size:0.7rem">{{ pos.category }}</span>
                      </td>
                      <td class="num pos-price">
                        {{ pos.base_price_pln ? pos.base_price_pln.toFixed(0)+' zł' : '—' }}
                      </td>
                      <td class="pos-actions">
                        <button class="btn btn-outline btn-sm"
                                @click="openPdf('/templates/'+pos.id+'/arkusz')">
                          PDF
                        </button>
                        <button class="btn btn-outline btn-sm"
                                @click="expandedPosId = expandedPosId === pos.id ? null : pos.id">
                          {{ expandedPosId === pos.id ? 'Zwiń' : 'Edytuj' }}
                        </button>
                      </td>
                    </tr>

                    <tr v-if="expandedPosId === pos.id">
                      <td colspan="5" class="project-editor-cell">
                        <div class="editor-grid">
                          <div>
                            <div class="editor-section-label">Operacje (wydział · godz · zł/h)</div>
                            <div v-if="!posEditOps[pos.id]?.length" class="editor-empty">Brak operacji — dodaj ręcznie</div>
                            <div v-for="(op, oi) in posEditOps[pos.id]" :key="oi" class="op-row">
                              <input v-model="op.op" placeholder="Wydział/czynność" class="editor-input">
                              <input v-model.number="op.hours" type="number" step="0.5" min="0" placeholder="h" class="editor-input editor-input--narrow">
                              <input v-model.number="op.rate" type="number" step="10" min="0" placeholder="zł/h" class="editor-input editor-input--narrow">
                              <button @click="posEditOps[pos.id].splice(oi,1)" class="btn-remove" title="Usuń">×</button>
                            </div>
                            <button class="btn btn-sm btn-outline" style="margin-top:4px"
                                    @click="posEditOps[pos.id] = [...(posEditOps[pos.id]||[]), {op:'',hours:0,rate:settings.labor_rate_pln}]">
                              + Dodaj operację
                            </button>
                            <div v-if="posEditOps[pos.id]?.length" class="editor-summary">
                              Robocizna: <strong>{{ posCalcLabor(pos.id).toFixed(0) }} zł</strong>
                            </div>
                          </div>

                          <div>
                            <div class="editor-section-label">Materiały (nazwa · wymiar · ilość · jm · kg)</div>
                            <div v-if="!posEditMats[pos.id]?.length" class="editor-empty">Brak materiałów — dodaj ręcznie</div>
                            <div v-for="(m, mi) in posEditMats[pos.id]" :key="mi" class="mat-row">
                              <input v-model="m.mat" placeholder="Nazwa materiału" class="editor-input">
                              <input v-model="m.dim" placeholder="Wymiar" class="editor-input editor-input--narrow">
                              <input v-model.number="m.qty" type="number" step="0.1" min="0" placeholder="ilość" class="editor-input editor-input--narrow">
                              <input v-model="m.unit" placeholder="jm" class="editor-input editor-input--xs">
                              <input v-model.number="m.mass_kg" type="number" step="0.01" min="0" placeholder="kg" class="editor-input editor-input--narrow">
                              <button @click="posEditMats[pos.id].splice(mi,1)" class="btn-remove" title="Usuń">×</button>
                            </div>
                            <button class="btn btn-sm btn-outline" style="margin-top:4px"
                                    @click="posEditMats[pos.id] = [...(posEditMats[pos.id]||[]), {mat:'',dim:'',qty:1,unit:'szt',mass_kg:null}]">
                              + Dodaj materiał
                            </button>
                          </div>

                          <div>
                            <div class="editor-section-label">Rysunek PDF</div>
                            <div v-if="pos.drawing_path" class="drawing-btns">
                              <button class="btn btn-outline btn-sm"
                                      @click="openPdf('/templates/'+pos.id+'/drawing')">
                                Podgląd
                              </button>
                              <button class="btn btn-outline btn-sm"
                                      @click="extractPosDrawing(pos, false)"
                                      :disabled="drawingExtractLoading[pos.id]">
                                {{ drawingExtractLoading[pos.id] ? 'Czytam...' : 'Czytaj rysunek' }}
                              </button>
                            </div>
                            <div v-else class="editor-empty" style="margin-bottom:8px">Brak rysunku</div>
                            <input type="file" accept=".pdf" :id="'drw_'+pos.id"
                                   @change="uploadPosDrawing(pos.id, $event)" style="font-size:0.8rem">
                          </div>
                        </div>

                        <div v-if="drawingExtract[pos.id]" class="drawing-readout">
                          <div class="drawing-readout-header">
                            <div>
                              <div class="drawing-readout-title">Odczyt z PDF</div>
                              <div class="drawing-readout-meta">
                                <strong>{{ drawingExtract[pos.id].name || pos.name }}</strong>
                                <span v-if="drawingExtract[pos.id].drawing_no"> · {{ drawingExtract[pos.id].drawing_no }}</span>
                                <span v-if="drawingExtract[pos.id].mass_kg"> · {{ drawingExtract[pos.id].mass_kg }} kg</span>
                              </div>
                            </div>
                            <button class="btn btn-primary btn-sm" @click="extractPosDrawing(pos, true)"
                                    :disabled="drawingExtractLoading[pos.id]">
                              Zastosuj do katalogu
                            </button>
                          </div>
                          <div v-if="drawingExtract[pos.id].materials_json?.length" style="margin-top:10px;overflow-x:auto">
                            <table class="data-table data-table--xs">
                              <thead>
                                <tr>
                                  <th>Poz.</th>
                                  <th>Materiał</th>
                                  <th>Wymiar/opis</th>
                                  <th class="num">Ilość</th>
                                </tr>
                              </thead>
                              <tbody>
                                <tr v-for="m in drawingExtract[pos.id].materials_json.slice(0,8)" :key="m.line_id || m.part_no || m.dim">
                                  <td class="pos-nr">{{ m.part_no || m.line_id || '—' }}</td>
                                  <td>{{ m.mat || '—' }}</td>
                                  <td>{{ m.dim || m.name || '—' }}</td>
                                  <td class="num">{{ m.qty || 1 }} {{ m.unit || 'szt' }}</td>
                                </tr>
                              </tbody>
                            </table>
                            <div v-if="drawingExtract[pos.id].materials_json.length > 8" class="bom-overflow">
                              + {{ drawingExtract[pos.id].materials_json.length - 8 }} kolejnych pozycji BOM
                            </div>
                          </div>
                          <div v-if="drawingExtract[pos.id].operations_json?.length" class="drawing-ops">
                            Operacje z rysunku: {{ drawingExtract[pos.id].operations_json.map(o => o.op || o.name).join(' · ') }}
                          </div>
                        </div>

                        <div class="editor-footer">
                          <button class="btn btn-primary btn-sm" @click="savePosHours(pos)">Zapisz operacje + materiały</button>
                          <button class="btn btn-outline btn-sm" @click="expandedPosId = null">Zamknij</button>
                          <span v-if="posSaveMsg[pos.id]" class="save-msg">
                            {{ posSaveMsg[pos.id] }}
                          </span>
                        </div>
                      </td>
                    </tr>
                  </template>
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.empty-state {
  text-align: center;
  padding: 32px;
  color: var(--muted);
}

.projects-list {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.proj-card {
  padding: 0;
  overflow: hidden;
}

.proj-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 14px 16px;
  background: var(--surface2);
  cursor: pointer;
}

.proj-code {
  font-weight: 700;
  font-size: 1rem;
}

.proj-count {
  color: var(--muted);
  font-size: 0.82rem;
  margin-left: 10px;
}

.proj-header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.proj-chevron {
  font-size: 0.75rem;
  color: var(--muted);
}

.loading-note {
  padding: 16px;
  color: var(--muted);
  text-align: center;
}

.cat-filter-bar {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  padding: 10px 16px;
  border-bottom: 1px solid var(--surface2);
}

.btn-ghost {
  background: var(--surface2);
}

/* position table cells */
.pos-nr {
  font-weight: 700;
  font-family: monospace;
}

.pos-notes {
  color: var(--muted);
  font-size: 0.75rem;
  margin-top: 2px;
}

.pos-price {
  font-weight: 600;
}

.pos-actions {
  text-align: center;
  white-space: nowrap;
  display: flex;
  gap: 4px;
  justify-content: center;
}

/* editor panel */
.editor-grid {
  display: grid;
  grid-template-columns: 1fr 1fr 200px;
  gap: 16px;
}

.editor-section-label {
  font-weight: 700;
  font-size: 0.82rem;
  margin-bottom: 8px;
  text-transform: uppercase;
  color: var(--muted);
}

.editor-empty {
  color: var(--muted);
  font-size: 0.8rem;
}

.editor-input {
  font-size: 0.8rem;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--card);
  color: var(--text);
  width: 100%;
}

.editor-input--narrow {
  padding: 4px 6px;
}

.editor-input--xs {
  padding: 4px 6px;
}

.op-row {
  display: grid;
  grid-template-columns: 1fr 60px 70px 28px;
  gap: 6px;
  margin-bottom: 6px;
  align-items: center;
}

.mat-row {
  display: grid;
  grid-template-columns: 1fr 82px 52px 46px 58px 28px;
  gap: 6px;
  margin-bottom: 6px;
  align-items: center;
}

.btn-remove {
  background: none;
  border: none;
  color: var(--rcm-red);
  cursor: pointer;
  font-size: 1.1rem;
  line-height: 1;
  padding: 0;
}

.editor-summary {
  margin-top: 10px;
  padding: 8px;
  background: var(--sl-50);
  border-radius: 6px;
  font-size: 0.82rem;
}

.drawing-btns {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 8px;
}

/* drawing readout — base styles (drawing-readout class is styled in style.css with !important) */
.drawing-readout-header {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: flex-start;
}

.drawing-readout-title {
  font-weight: 800;
  color: var(--rcm-blue);
  font-size: 0.85rem;
  text-transform: uppercase;
}

.drawing-readout-meta {
  font-size: 0.82rem;
  margin-top: 4px;
}

.data-table--xs {
  font-size: 0.78rem;
}

.bom-overflow {
  font-size: 0.75rem;
  color: var(--muted);
  margin-top: 4px;
}

.drawing-ops {
  margin-top: 8px;
  font-size: 0.78rem;
  color: var(--muted);
}

.editor-footer {
  margin-top: 12px;
  display: flex;
  gap: 8px;
  align-items: center;
}

.save-msg {
  color: var(--rcm-green);
  font-size: 0.82rem;
}
</style>
