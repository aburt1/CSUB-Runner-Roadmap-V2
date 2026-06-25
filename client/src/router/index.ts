import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: HomeView },
    { path: '/admin', component: () => import('../pages/admin/AdminPage.vue') },
    { path: '/admin/local-login', component: () => import('../pages/admin/AdminLocalLogin.vue') },
    // Unknown paths (stale links, typos) bounce to home instead of rendering a blank
    // <router-view>. nginx already serves index.html for any path, so the SPA owns 404s.
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
