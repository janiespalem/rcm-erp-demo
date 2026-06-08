<script setup>
import { ref, onMounted } from 'vue'
import { api } from '@/composables/useApi'
import { useToast } from '@/composables/useToast'
import { useSettings } from '@/composables/useSettings'
import { useConfirm } from '@/composables/useConfirm'

const operationCatalog = ref([])
const { show } = useToast()
const { settings } = useSettings()
const { confirm } = useConfirm()

function emptyDraft() {
  return { name: '', department: '', default_rate: settings.value.labor_rate_pln, formula: '' }
}

const draft = ref(emptyDraft())

async function load() { operationCatalog.value = await api('/operation-catalog/') }
onMounted(load)

async function save(op = null) {
  const payload = op || draft.value
  if (!payload.name) { show('Podaj nazwę operacji'); return }
  if (op?.id) {
    await api(`/operation-catalog/${op.id}`, { method: 'PATCH', body: payload })
  } else {
    await api('/operation-catalog/', { method: 'POST', body: payload })
    draft.value = emptyDraft()
  }
  await load()
}

async function remove(op) {
  if (!await confirm(`Usunąć operację "${op.name}"?`)) return
  await api(`/operation-catalog/${op.id}`, { method: 'DELETE' })
  await load()
}
</script>

<template>
  <div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">
      <h2 style="font-size:1rem;font-weight:700;color:var(--rcm-blue)">Operacje, wydziały i stawki</h2>
      <button class="btn btn-outline btn-sm" @click="load()">🔄 Odśwież</button>
    </div>
    <div class="card" style="margin-bottom:14px">
      <div style="font-weight:700;margin-bottom:10px;color:var(--rcm-blue)">Dodaj operację</div>
      <div class="manager-form-grid">
        <div class="form-group"><label>Nazwa operacji</label><input v-model="draft.name" placeholder="np. Cięcie plazmą"></div>
        <div class="form-group"><label>Wydział</label><input v-model="draft.department" placeholder="np. CNC / Plazma"></div>
        <div class="form-group"><label>PLN/h</label><input type="number" v-model.number="draft.default_rate" min="0" step="5"></div>
        <button class="btn btn-primary" @click="save()">Dodaj</button>
      </div>
    </div>
    <div class="card">
      <div v-if="!operationCatalog.length" style="padding:18px;color:var(--muted);text-align:center">Brak operacji</div>
      <div v-else class="manager-table-wrap">
        <table class="manager-table" style="width:100%;border-collapse:collapse;font-size:0.84rem">
          <thead>
            <tr style="background:var(--surface2);text-align:left">
              <th style="padding:8px 10px">Operacja</th>
              <th style="padding:8px 10px">Wydział</th>
              <th style="padding:8px 10px;width:110px">PLN/h</th>
              <th style="padding:8px 10px;width:120px;text-align:right">Akcje</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="op in operationCatalog" :key="op.id" style="border-bottom:1px solid var(--surface2)">
              <td style="padding:7px 10px"><input v-model="op.name" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px"><input v-model="op.department" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px"><input type="number" v-model.number="op.default_rate" min="0" step="5" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px;text-align:right;white-space:nowrap">
                <button class="btn btn-outline btn-sm" @click="save(op)">Zapisz</button>
                <button class="btn btn-outline btn-sm" style="color:var(--rcm-red)" @click="remove(op)">Usuń</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
