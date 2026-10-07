<script setup>
import { ref } from 'vue'
import { api } from '@/composables/useApi'
import { useTemplates } from '@/composables/useTemplates'
import { useToast } from '@/composables/useToast'
import { useConfirm } from '@/composables/useConfirm'

const SOP_LIBRARY = [
  'Oczyścić powierzchnię z rdzy i zanieczyszczeń',
  'Ocenić zakres uszkodzeń i udokumentować',
  'Pobrać materiał wg. listy materiałowej',
  'Wyciąć uszkodzone elementy (PIŁA / PLAZMA)',
  'Ciąć pręty na wymiar wg. rysunku',
  'Wykonać cięcie CNC wg. pliku DXF',
  'Giąć strzemiona wg. wymiarów (a, b, c)',
  'Giąć elementy na giętarce CNC',
  'Wspawać nowy element wg. pozycji fabrycznej (MIG)',
  'Wspawać wstawki — pełny przetop',
  'Zeszlifować spoiny do gładkości (SM)',
  'Montować kosz zbrojeniowy na stole montażowym',
  'Wiązać węzły drutem wiązałkowym co 200mm',
  'Zmontować elementy śrubowe i osie',
  'Sprawdzić wymiary pierwszej sztuki kontrolnej',
  'Sprawdzić wymiary liniowe wg. rysunku',
  'Sprawdzić wymiary geometryczne',
  'Sprawdzenie wymogów powierzchni',
  'Nałożyć powłokę antykorozyjną',
  'Skompletować elementy wg. listy zbiorczej',
  'Oznaczyć pojemnik numerem zlecenia',
]

const { templates, loadTemplates } = useTemplates()
const { show } = useToast()
const { confirm } = useConfirm()

const showNew = ref(false)
const newTemplate = ref({ name: '', category: 'remont', base_price_pln: null, instruction_blocks: [] })

function addSopBlock(text) {
  const blks = newTemplate.value.instruction_blocks
  blks.push({ order: blks.length + 1, text })
}

function removeSopBlock(i) {
  newTemplate.value.instruction_blocks.splice(i, 1)
}

async function saveTemplate() {
  await api('/templates', { method: 'POST', body: newTemplate.value })
  showNew.value = false
  newTemplate.value = { name: '', category: 'remont', base_price_pln: null, instruction_blocks: [] }
  loadTemplates()
}

async function saveTemplatePatch(t) {
  if (!t.name) { show('Podaj nazwę szablonu'); return }
  await api(`/templates/${t.id}`, {
    method: 'PATCH',
    body: {
      name: t.name,
      category: t.category,
      base_price_pln: t.base_price_pln || null,
      notes: t.notes || null,
      operations_json: t.operations_json || [],
      materials_json: t.materials_json || [],
      instruction_blocks: t.instruction_blocks || [],
      machines_json: t.machines_json || [],
      margin_pct: t.margin_pct ?? 0.25,
      project_code: t.project_code || null,
      position_nr: t.position_nr || null,
    },
  })
  await loadTemplates()
}

async function deleteTemplate(t) {
  if (!await confirm(`Usunąć szablon "${t.name}" na stałe z bazy?`)) return
  await api(`/templates/${t.id}`, { method: 'DELETE' })
  await loadTemplates()
}
</script>

<template>
  <div class="card">
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px">
      <h2 style="margin-bottom:0">Katalog SOP</h2>
      <button class="btn btn-primary btn-sm" @click="showNew = true">+ Nowy szablon</button>
    </div>

    <div v-if="showNew" style="background:var(--bg);padding:16px;border-radius:var(--radius);margin-bottom:16px">
      <h2>Nowy szablon SOP</h2>
      <div class="form-row">
        <div class="form-group">
          <label>Nazwa szablonu</label>
          <input v-model="newTemplate.name" placeholder="np. Wymiana zęba w łyżce">
        </div>
        <div class="form-group">
          <label>Kategoria</label>
          <select v-model="newTemplate.category">
            <option value="remont">Remont</option>
            <option value="usługa">Usługa</option>
            <option value="zbrojenie">Zbrojenie</option>
            <option value="prefabrykat">Prefabrykat</option>
          </select>
        </div>
        <div class="form-group">
          <label>Cena bazowa (PLN)</label>
          <input type="number" v-model.number="newTemplate.base_price_pln">
        </div>
      </div>

      <label style="font-size:0.82rem;font-weight:600;color:var(--muted);text-transform:uppercase">
        Bloki instrukcji (klikaj aby dodać)
      </label>
      <div class="sop-blocks">
        <div class="sop-block" v-for="blk in SOP_LIBRARY" :key="blk" @click="addSopBlock(blk)">{{ blk }}</div>
      </div>
      <div class="sop-selected" v-if="newTemplate.instruction_blocks.length">
        <div class="sop-item" v-for="(b, i) in newTemplate.instruction_blocks" :key="i">
          <span class="sop-num">{{ i+1 }}.</span>
          <span style="flex:1">{{ b.text }}</span>
          <button class="btn btn-sm" style="background:none;color:var(--rcm-red)" @click="removeSopBlock(i)">✕</button>
        </div>
      </div>

      <div style="display:flex;gap:8px;margin-top:16px">
        <button class="btn btn-primary" @click="saveTemplate" :disabled="!newTemplate.name">Zapisz szablon</button>
        <button class="btn btn-outline" @click="showNew = false">Anuluj</button>
      </div>
    </div>

    <div class="manager-table-wrap">
      <table class="manager-table">
        <thead>
          <tr>
            <th>Nazwa</th>
            <th>Kategoria</th>
            <th>Cena bazowa</th>
            <th>Notatki</th>
            <th style="text-align:right">Akcje</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="t in templates" :key="t.id">
            <td><input v-model="t.name" style="font-size:0.82rem"></td>
            <td>
              <select v-model="t.category" style="font-size:0.82rem">
                <option value="remont">remont</option>
                <option value="usługa">usługa</option>
                <option value="zbrojenie">zbrojenie</option>
                <option value="prefabrykat">prefabrykat</option>
                <option value="ciecie">ciecie</option>
              </select>
            </td>
            <td><input type="number" v-model.number="t.base_price_pln" min="0" step="1" style="font-size:0.82rem"></td>
            <td>
              <input v-model="t.notes" :placeholder="`${(t.instruction_blocks||[]).length} bloków SOP`" style="font-size:0.82rem">
            </td>
            <td style="text-align:right;white-space:nowrap">
              <button class="btn btn-outline btn-sm" @click="saveTemplatePatch(t)">Zapisz</button>
              <button class="btn btn-outline btn-sm" style="color:var(--rcm-red)" @click="deleteTemplate(t)">Usuń</button>
            </td>
          </tr>
          <tr v-if="templates.length === 0">
            <td colspan="5" style="text-align:center;color:var(--muted);padding:24px">Brak szablonów</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
