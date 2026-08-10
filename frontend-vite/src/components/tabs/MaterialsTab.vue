<script setup>
import { ref, onMounted } from 'vue'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'

const { approvedMaterials, loadApprovedMaterials, saveMaterial, deleteMaterial } = useApprovedMaterials()
const draft = ref({ name: '', category: 'zbrojenie', default_rate_pln_kg: 4.5, notes: '' })

onMounted(loadApprovedMaterials)

async function addMaterial() {
  await saveMaterial(null, draft.value)
  draft.value = { name: '', category: 'zbrojenie', default_rate_pln_kg: 4.5, notes: '' }
}
</script>

<template>
  <div>
    <div class="tab-toolbar">
      <span></span>
      <button class="btn btn-outline btn-sm" @click="loadApprovedMaterials()">Odśwież</button>
    </div>
    <div class="card">
      <div style="font-weight:700;margin-bottom:10px;color:var(--rcm-blue)">Dodaj materiał</div>
      <div class="manager-form-grid">
        <div class="form-group"><label>Nazwa / wymiar</label><input v-model="draft.name" placeholder="np. S355JR zbrojeniowy Ø12/Ø6"></div>
        <div class="form-group"><label>Kategoria</label><input v-model="draft.category" placeholder="np. zbrojenie / stal / blacha"></div>
        <div class="form-group"><label>PLN/kg</label><input type="number" v-model.number="draft.default_rate_pln_kg" min="0" step="0.10"></div>
        <button class="btn btn-primary" @click="addMaterial()">Dodaj</button>
      </div>
      <div class="section-sep"></div>
      <div v-if="!approvedMaterials.length" style="padding:18px;color:var(--muted);text-align:center">Brak materiałów</div>
      <div v-else class="manager-table-wrap">
        <table class="manager-table">
          <thead>
            <tr>
              <th>Materiał / wymiar</th>
              <th style="width:160px">Kategoria</th>
              <th class="num" style="width:110px">PLN/kg</th>
              <th class="num" style="width:120px">Akcje</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="mat in approvedMaterials" :key="mat.id">
              <td><input v-model="mat.name"></td>
              <td><input v-model="mat.category"></td>
              <td class="num"><input type="number" v-model.number="mat.default_rate_pln_kg" min="0" step="0.10"></td>
              <td class="num" style="white-space:nowrap">
                <button class="btn btn-outline btn-sm" @click="saveMaterial(mat)">Zapisz</button>
                <button class="btn btn-outline btn-sm" style="color:var(--rcm-red)" @click="deleteMaterial(mat)">Usuń</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
