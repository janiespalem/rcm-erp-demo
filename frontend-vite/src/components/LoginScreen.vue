<script setup>
import { useAuth } from '@/composables/useAuth'

const { loginRole, loginPin, loginError, login } = useAuth()
</script>

<template>
  <div class="login-bg">
    <div class="login-card">
      <div class="login-logo">
        <div>
          <div class="login-brand">FactoryFlow ERP Demo</div>
          <div class="login-sub">System zarządzania zleceniami</div>
        </div>
      </div>
      <div class="form-group" style="margin-bottom:14px">
        <label>Rola</label>
        <select v-model="loginRole">
          <option value="biuro">Biuro</option>
          <option value="technolog">Technolog</option>
          <option value="ceo">CEO</option>
          <option value="dyrektor_produkcji">Dyrektor Produkcji</option>
        </select>
      </div>
      <div class="form-group" style="margin-bottom:20px">
        <label>PIN</label>
        <input
          type="password" v-model="loginPin" placeholder="••••"
          @keyup.enter="login" maxlength="4"
          style="font-size:1.4rem;letter-spacing:0.3em;text-align:center"
        >
      </div>
      <button class="btn btn-primary" style="width:100%;justify-content:center;padding:11px" @click="login">
        Zaloguj się
      </button>
      <p v-if="loginError" style="color:var(--rcm-red);text-align:center;margin-top:10px;font-size:0.85rem;font-weight:600">
        Błędny PIN — spróbuj ponownie
      </p>
    </div>
  </div>
</template>
