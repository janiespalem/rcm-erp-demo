<script setup>
import { ref, onMounted } from 'vue'
import { api, openPdf } from '@/composables/useApi'
import { useSettings } from '@/composables/useSettings'
import { useToast } from '@/composables/useToast'

const API_BASE = `${location.origin}/api`

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
  posSaveMsg.value = { ...posSaveMsg.value, [pos.id]: 'Zapisano ✓' }
  setTimeout(() => { posSaveMsg.value = { ...posSaveMsg.value, [pos.id]: '' } }, 3000)
  const all = await api(`/templates?project_code=${encodeURIComponent(pos.project_code || '')}`)
  projPositions.value = { ...projPositions.value, [pos.project_code]: all }
}

async function uploadPosDrawing(posId, event) {
  const file = event.target.files[0]
  if (!file) return
  const fd = new FormData()
  fd.append('file', file)
  const stored = localStorage.getItem('rcm_user')
  const token = stored ? JSON.parse(stored).token : null
  const headers = token ? { Authorization: `Bearer ${token}` } : {}
  await fetch(`${API_BASE}/templates/${posId}/drawing`, { method: 'POST', body: fd, headers })
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
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">
      <h2 style="font-size:1rem;font-weight:700;color:var(--rcm-blue)">Katalog projektów</h2>
      <button class="btn btn-outline btn-sm" @click="load()">🔄 Odśwież</button>
    </div>

    <div v-if="!projectsList.length" style="text-align:center;padding:32px;color:var(--muted)">
      Brak projektów w katalogu
    </div>

    <div v-else style="display:flex;flex-direction:column;gap:16px">
      <div v-for="proj in projectsList" :key="proj.project_code" class="card" style="padding:0;overflow:hidden">
        <div style="display:flex;justify-content:space-between;align-items:center;padding:14px 16px;background:var(--surface2);cursor:pointer"
             @click="toggleProjPositions(proj.project_code)">
          <div>
            <span style="font-weight:700;font-size:1rem">{{ proj.project_code }}</span>
            <span style="color:var(--muted);font-size:0.82rem;margin-left:10px">{{ proj.positions_count }} pozycji</span>
          </div>
          <div style="display:flex;gap:8px;align-items:center">
            <button class="btn btn-outline btn-sm" @click.stop="openPdf('/projects/'+proj.project_code+'/arkusze')">
              Drukuj wszystkie
            </button>
            <span>{{ expandedProj === proj.project_code ? '▲' : '▼' }}</span>
          </div>
        </div>

        <div v-if="expandedProj === proj.project_code">
          <div v-if="!projPositions[proj.project_code]" style="padding:16px;color:var(--muted);text-align:center">
            Ładowanie...
          </div>
          <div v-else>
            <div style="display:flex;gap:6px;flex-wrap:wrap;padding:10px 16px;border-bottom:1px solid var(--surface2)">
              <button v-for="cat in projCategories(proj.project_code)" :key="cat"
                      class="btn btn-sm"
                      :style="projCatFilter === cat ? 'background:var(--rcm-blue);color:#fff' : 'background:var(--surface2)'"
                      @click="projCatFilter = projCatFilter === cat ? '' : cat">
                {{ cat }}
              </button>
            </div>
            <div style="overflow-x:auto">
              <table style="width:100%;border-collapse:collapse;font-size:0.82rem">
                <thead>
                  <tr style="background:var(--surface2);text-align:left">
                    <th style="padding:7px 12px;width:100px">Poz.</th>
                    <th style="padding:7px 12px">Nazwa / Materiał</th>
                    <th style="padding:7px 12px">Kategoria</th>
                    <th style="padding:7px 12px;text-align:right">Cena/szt.</th>
                    <th style="padding:7px 12px;text-align:center">Akcje</th>
                  </tr>
                </thead>
                <tbody>
                  <template v-for="pos in filteredPositions(proj.project_code)" :key="pos.id">
                    <tr style="border-bottom:1px solid var(--surface2)"
                        :class="{'project-row-open': expandedPosId === pos.id}"
                        :style="expandedPosId === pos.id ? 'background:#111923' : ''">
                      <td style="padding:7px 12px;font-weight:700;font-family:monospace">{{ pos.position_nr || '—' }}</td>
                      <td style="padding:7px 12px">
                        <div>{{ pos.name }}</div>
                        <div v-if="pos.notes" style="color:var(--muted);font-size:0.75rem;margin-top:2px">{{ pos.notes }}</div>
                      </td>
                      <td style="padding:7px 12px">
                        <span class="badge badge-draft" style="font-size:0.7rem">{{ pos.category }}</span>
                      </td>
                      <td style="padding:7px 12px;text-align:right;font-weight:600">
                        {{ pos.base_price_pln ? pos.base_price_pln.toFixed(0)+' zł' : '—' }}
                      </td>
                      <td style="padding:7px 12px;text-align:center;white-space:nowrap">
                        <button class="btn btn-outline btn-sm" style="margin-right:4px"
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
                        <div style="display:grid;grid-template-columns:1fr 1fr 200px;gap:16px">
                          <div>
                            <div style="font-weight:700;font-size:0.82rem;margin-bottom:8px;text-transform:uppercase;color:var(--muted)">Operacje (wydział · godz · zł/h)</div>
                            <div v-if="!posEditOps[pos.id]?.length" style="color:var(--muted);font-size:0.8rem">Brak operacji — dodaj ręcznie</div>
                            <div v-for="(op, oi) in posEditOps[pos.id]" :key="oi"
                                 style="display:grid;grid-template-columns:1fr 60px 70px 28px;gap:6px;margin-bottom:6px;align-items:center">
                              <input v-model="op.op" placeholder="Wydział/czynność"
                                     style="font-size:0.8rem;padding:4px 8px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model.number="op.hours" type="number" step="0.5" min="0" placeholder="h"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model.number="op.rate" type="number" step="10" min="0" placeholder="zł/h"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <button @click="posEditOps[pos.id].splice(oi,1)"
                                      style="background:none;border:none;color:var(--rcm-red);cursor:pointer;font-size:1rem">✕</button>
                            </div>
                            <button class="btn btn-sm btn-outline" style="margin-top:4px"
                                    @click="posEditOps[pos.id] = [...(posEditOps[pos.id]||[]), {op:'',hours:0,rate:settings.labor_rate_pln}]">
                              + Dodaj operację
                            </button>
                            <div v-if="posEditOps[pos.id]?.length" class="editor-summary"
                                 style="margin-top:10px;padding:8px;background:#e8f0fe;border-radius:6px;font-size:0.82rem">
                              Robocizna: <strong>{{ posCalcLabor(pos.id).toFixed(0) }} zł</strong>
                            </div>
                          </div>

                          <div>
                            <div style="font-weight:700;font-size:0.82rem;margin-bottom:8px;text-transform:uppercase;color:var(--muted)">Materiały (nazwa · wymiar · ilość · jm · kg)</div>
                            <div v-if="!posEditMats[pos.id]?.length" style="color:var(--muted);font-size:0.8rem">Brak materiałów — dodaj ręcznie</div>
                            <div v-for="(m, mi) in posEditMats[pos.id]" :key="mi"
                                 style="display:grid;grid-template-columns:1fr 82px 52px 46px 58px 28px;gap:6px;margin-bottom:6px;align-items:center">
                              <input v-model="m.mat" placeholder="Nazwa materiału"
                                     style="font-size:0.8rem;padding:4px 8px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model="m.dim" placeholder="Wymiar"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model.number="m.qty" type="number" step="0.1" min="0" placeholder="ilość"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model="m.unit" placeholder="jm"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <input v-model.number="m.mass_kg" type="number" step="0.01" min="0" placeholder="kg"
                                     style="font-size:0.8rem;padding:4px 6px;border:1px solid var(--surface2);border-radius:4px">
                              <button @click="posEditMats[pos.id].splice(mi,1)"
                                      style="background:none;border:none;color:var(--rcm-red);cursor:pointer;font-size:1rem">✕</button>
                            </div>
                            <button class="btn btn-sm btn-outline" style="margin-top:4px"
                                    @click="posEditMats[pos.id] = [...(posEditMats[pos.id]||[]), {mat:'',dim:'',qty:1,unit:'szt',mass_kg:null}]">
                              + Dodaj materiał
                            </button>
                          </div>

                          <div>
                            <div style="font-weight:700;font-size:0.82rem;margin-bottom:8px;text-transform:uppercase;color:var(--muted)">Rysunek PDF</div>
                            <div v-if="pos.drawing_path" style="margin-bottom:8px">
                              <button class="btn btn-outline btn-sm" style="margin-bottom:6px"
                                      @click="openPdf('/templates/'+pos.id+'/drawing')">
                                Podgląd
                              </button>
                              <button class="btn btn-outline btn-sm" style="margin-top:6px"
                                      @click="extractPosDrawing(pos, false)"
                                      :disabled="drawingExtractLoading[pos.id]">
                                {{ drawingExtractLoading[pos.id] ? 'Czytam...' : 'Czytaj rysunek' }}
                              </button>
                            </div>
                            <div v-else style="color:var(--muted);font-size:0.8rem;margin-bottom:8px">Brak rysunku</div>
                            <input type="file" accept=".pdf" :id="'drw_'+pos.id"
                                   @change="uploadPosDrawing(pos.id, $event)" style="font-size:0.8rem">
                          </div>
                        </div>

                        <div v-if="drawingExtract[pos.id]" class="drawing-readout"
                             style="margin-top:14px;padding:12px;border:1px solid #b8cef0;background:#f1f6ff;border-radius:6px">
                          <div style="display:flex;justify-content:space-between;gap:12px;align-items:flex-start">
                            <div>
                              <div style="font-weight:800;color:var(--rcm-blue);font-size:0.85rem;text-transform:uppercase">Odczyt z PDF</div>
                              <div style="font-size:0.82rem;margin-top:4px">
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
                            <table style="width:100%;font-size:0.78rem;border-collapse:collapse">
                              <thead>
                                <tr style="background:#e6eefc">
                                  <th style="padding:5px 6px;text-align:left">Poz.</th>
                                  <th style="padding:5px 6px;text-align:left">Materiał</th>
                                  <th style="padding:5px 6px;text-align:left">Wymiar/opis</th>
                                  <th style="padding:5px 6px;text-align:right">Ilość</th>
                                </tr>
                              </thead>
                              <tbody>
                                <tr v-for="m in drawingExtract[pos.id].materials_json.slice(0,8)" :key="m.line_id || m.part_no || m.dim">
                                  <td style="padding:5px 6px;font-family:monospace">{{ m.part_no || m.line_id || '—' }}</td>
                                  <td style="padding:5px 6px">{{ m.mat || '—' }}</td>
                                  <td style="padding:5px 6px">{{ m.dim || m.name || '—' }}</td>
                                  <td style="padding:5px 6px;text-align:right">{{ m.qty || 1 }} {{ m.unit || 'szt' }}</td>
                                </tr>
                              </tbody>
                            </table>
                            <div v-if="drawingExtract[pos.id].materials_json.length > 8"
                                 style="font-size:0.75rem;color:var(--muted);margin-top:4px">
                              + {{ drawingExtract[pos.id].materials_json.length - 8 }} kolejnych pozycji BOM
                            </div>
                          </div>
                          <div v-if="drawingExtract[pos.id].operations_json?.length"
                               style="margin-top:8px;font-size:0.78rem;color:#334155">
                            Operacje z rysunku: {{ drawingExtract[pos.id].operations_json.map(o => o.op || o.name).join(' · ') }}
                          </div>
                        </div>

                        <div style="margin-top:12px;display:flex;gap:8px">
                          <button class="btn btn-primary btn-sm" @click="savePosHours(pos)">Zapisz operacje + materiały</button>
                          <button class="btn btn-outline btn-sm" @click="expandedPosId = null">Zamknij</button>
                          <span v-if="posSaveMsg[pos.id]" style="color:var(--rcm-green);font-size:0.82rem;align-self:center">
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
