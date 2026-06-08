import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'

// Same three routes as the old React app.
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: HomeView },
    { path: '/admin', component: () => import('../pages/admin/AdminPage.vue') },
    { path: '/admin/local-login', component: () => import('../pages/admin/AdminLocalLogin.vue') },
  ],
})
