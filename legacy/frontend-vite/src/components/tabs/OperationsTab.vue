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
    <div class="tab-toolbar">
      <span></span>
      <button class="btn btn-outline btn-sm" @click="load()">Odśwież</button>
    </div>
    <div class="card">
      <div style="font-weight:700;margin-bottom:10px;color:var(--rcm-blue)">Dodaj operację</div>
      <div class="manager-form-grid">
        <div class="form-group"><label>Nazwa operacji</label><input v-model="draft.name" placeholder="np. Cięcie plazmą"></div>
        <div class="form-group"><label>Wydział</label><input v-model="draft.department" placeholder="np. CNC / Plazma"></div>
        <div class="form-group"><label>PLN/h</label><input type="number" v-model.number="draft.default_rate" min="0" step="5"></div>
        <button class="btn btn-primary" @click="save()">Dodaj</button>
      </div>
      <div class="section-sep"></div>
      <div v-if="!operationCatalog.length" style="padding:18px;color:var(--muted);text-align:center">Brak operacji</div>
      <div v-else class="manager-table-wrap">
        <table class="manager-table">
          <thead>
            <tr>
              <th>Operacja</th>
              <th>Wydział</th>
              <th class="num" style="width:110px">PLN/h</th>
              <th class="num" style="width:120px">Akcje</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="op in operationCatalog" :key="op.id">
              <td><input v-model="op.name"></td>
              <td><input v-model="op.department"></td>
              <td class="num"><input type="number" v-model.number="op.default_rate" min="0" step="5"></td>
              <td class="num" style="white-space:nowrap">
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
