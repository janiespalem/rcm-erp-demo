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
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">
      <h2 style="font-size:1rem;font-weight:700;color:var(--rcm-blue)">Materiały, średnice i ceny</h2>
      <button class="btn btn-outline btn-sm" @click="loadApprovedMaterials()">🔄 Odśwież</button>
    </div>
    <div class="card" style="margin-bottom:14px">
      <div style="font-weight:700;margin-bottom:10px;color:var(--rcm-blue)">Dodaj materiał</div>
      <div class="manager-form-grid">
        <div class="form-group"><label>Nazwa / wymiar</label><input v-model="draft.name" placeholder="np. S355JR zbrojeniowy Ø12/Ø6"></div>
        <div class="form-group"><label>Kategoria</label><input v-model="draft.category" placeholder="np. zbrojenie / stal / blacha"></div>
        <div class="form-group"><label>PLN/kg</label><input type="number" v-model.number="draft.default_rate_pln_kg" min="0" step="0.10"></div>
        <button class="btn btn-primary" @click="addMaterial()">Dodaj</button>
      </div>
    </div>
    <div class="card">
      <div v-if="!approvedMaterials.length" style="padding:18px;color:var(--muted);text-align:center">Brak materiałów</div>
      <div v-else class="manager-table-wrap">
        <table class="manager-table" style="width:100%;border-collapse:collapse;font-size:0.84rem">
          <thead>
            <tr style="background:var(--surface2);text-align:left">
              <th style="padding:8px 10px">Materiał / wymiar</th>
              <th style="padding:8px 10px;width:160px">Kategoria</th>
              <th style="padding:8px 10px;width:110px">PLN/kg</th>
              <th style="padding:8px 10px;width:120px;text-align:right">Akcje</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="mat in approvedMaterials" :key="mat.id" style="border-bottom:1px solid var(--surface2)">
              <td style="padding:7px 10px"><input v-model="mat.name" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px"><input v-model="mat.category" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px"><input type="number" v-model.number="mat.default_rate_pln_kg" min="0" step="0.10" style="font-size:0.82rem"></td>
              <td style="padding:7px 10px;text-align:right;white-space:nowrap">
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
