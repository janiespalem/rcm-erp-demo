<script setup>
import { useAuth } from '@/composables/useAuth'

const { loginRole, loginPin, loginError, login } = useAuth()
</script>

<template>
  <div class="login-bg">
    <div class="login-card">
      <div class="login-logo">
        <div class="demo-mark" aria-hidden="true">FF</div>
        <div>
          <div class="login-brand">FactoryFlow ERP Demo</div>
          <div class="login-sub">Manufacturing workflow portfolio</div>
        </div>
      </div>
      <div class="form-group" style="margin-bottom:14px">
        <label>Rola</label>
        <select v-model="loginRole">
          <option value="biuro">Biuro</option>
          <option value="technolog">Technolog</option>
          <option value="ceo">CEO</option>
          <option value="produkcja">Produkcja</option>
        </select>
      </div>
      <div class="form-group" style="margin-bottom:20px">
        <label>PIN</label>
        <input
          type="password" inputmode="numeric" pattern="[0-9]*" v-model="loginPin" placeholder="••••"
          @keyup.enter="login" maxlength="4"
          style="font-size:1.4rem;letter-spacing:0.3em;text-align:center"
        >
      </div>
      <button class="btn btn-primary" style="width:100%;justify-content:center;padding:11px" @click="login">
        Zaloguj się
      </button>
      <p class="demo-access">Dane fikcyjne · Demo PIN: Biuro 1111 · Technolog 2222 · CEO 3333<br>Produkcja: zmiana I 4444 · zmiana II 5555</p>
      <p v-if="loginError" style="color:var(--rcm-red);text-align:center;margin-top:10px;font-size:0.85rem;font-weight:600">
        Błędny PIN — spróbuj ponownie
      </p>
    </div>
  </div>
</template>

<style scoped>
.demo-mark {
  width: 42px;
  height: 42px;
  display: grid;
  place-items: center;
  border-radius: 10px;
  background: var(--rcm-blue);
  color: white;
  font-weight: 850;
  letter-spacing: -0.04em;
}
.demo-access {
  margin: 14px 0 0;
  color: var(--text-muted);
  font-size: 0.78rem;
  line-height: 1.45;
  text-align: center;
}
</style>
