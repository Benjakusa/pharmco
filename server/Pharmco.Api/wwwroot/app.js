(() => {
  "use strict";

  const state = { accessToken: "", refreshToken: "", user: null, products: [], users: [] };
  const $ = (selector) => document.querySelector(selector);
  const loginScreen = $("#login-screen");
  const appScreen = $("#app-screen");

  async function api(path, options = {}, allowRefresh = true) {
    const headers = new Headers(options.headers || {});
    if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    if (state.accessToken) headers.set("Authorization", `Bearer ${state.accessToken}`);
    const response = await fetch(path, { ...options, headers });
    if (response.status === 401 && allowRefresh && state.refreshToken && path !== "/api/auth/refresh") {
      const renewed = await fetch("/api/auth/refresh", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refresh_token: state.refreshToken })
      });
      if (renewed.ok) {
        const tokens = await renewed.json();
        state.accessToken = tokens.access_token;
        state.refreshToken = tokens.refresh_token;
        return api(path, options, false);
      }
      clearSession();
      throw new Error("Your session expired. Please sign in again.");
    }
    if (!response.ok) {
      let message = `Request failed (${response.status})`;
      try {
        const body = await response.json();
        message = body?.error?.message || body?.message || message;
      } catch { /* response did not contain JSON */ }
      throw new Error(message);
    }
    if (response.status === 204) return null;
    return response.json();
  }

  function showError(element, message) {
    element.textContent = message;
    element.hidden = false;
  }

  function clearSession() {
    state.accessToken = "";
    state.refreshToken = "";
    state.user = null;
    state.products = [];
    state.users = [];
    appScreen.hidden = true;
    loginScreen.hidden = false;
    $("#password").value = "";
    $("#login-button").disabled = false;
    $("#login-button").innerHTML = 'Sign in <span aria-hidden="true">→</span>';
  }

  function money(amount) {
    return new Intl.NumberFormat("en-KE", { style: "currency", currency: "KES", maximumFractionDigits: 2 }).format(Number(amount || 0));
  }

  function safe(value) {
    return String(value ?? "").replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);
  }

  function stockClass(product) {
    return Number(product.stock_qty) <= Number(product.reorder_level) ? "stock-low" : "stock-ok";
  }

  function stockLabel(product) {
    const quantity = Number(product.stock_qty || 0);
    if (quantity <= Number(product.reorder_level || 0)) return "Low stock";
    return "In stock";
  }

  function productRows(products, overview = false) {
    if (!products.length) return `<tr><td colspan="${overview ? 5 : 6}" class="empty-cell">No products found for this pharmacy.</td></tr>`;
    return products.map((product) => {
      const identity = `<div class="product-name">${safe(product.name)}</div><div class="product-meta">${safe(product.unit || "piece")}</div>`;
      const category = safe(product.category || "Uncategorised");
      const barcode = safe(product.barcode || "—");
      const price = money(product.selling_price);
      const stock = `<span class="stock-pill ${stockClass(product)}">${safe(product.stock_qty)} ${safe(product.unit || "")}</span>`;
      const status = `<span class="stock-pill ${stockClass(product)}">${stockLabel(product)}</span>`;
      return `<tr><td>${identity}</td>${overview ? "" : `<td>${barcode}</td>`}<td>${category}</td><td>${price}</td><td>${stock}</td><td>${status}</td></tr>`;
    }).join("");
  }

  function renderDashboard() {
    const activeProducts = state.products.filter((product) => product.is_active !== false);
    const lowStock = activeProducts.filter((product) => Number(product.stock_qty) <= Number(product.reorder_level));
    $("#metric-products").textContent = String(activeProducts.length);
    $("#metric-low-stock").textContent = String(lowStock.length);
    $("#metric-team").textContent = state.user?.role === "admin" ? String(state.users.filter((user) => user.is_active).length) : "—";
    const overview = [...lowStock, ...activeProducts.filter((product) => !lowStock.includes(product))].slice(0, 5);
    $("#overview-products").innerHTML = productRows(overview, true);
  }

  function renderInventory() {
    const query = $("#inventory-search").value.trim().toLowerCase();
    const visible = state.products.filter((product) => {
      const haystack = `${product.name || ""} ${product.barcode || ""} ${product.category || ""}`.toLowerCase();
      return haystack.includes(query);
    });
    $("#inventory-count").textContent = `${visible.length} ${visible.length === 1 ? "item" : "items"}`;
    $("#inventory-products").innerHTML = productRows(visible);
  }

  function renderTeam() {
    const rows = state.users;
    $("#team-users").innerHTML = rows.length ? rows.map((user) => {
      const lastLogin = user.last_login_at ? new Date(user.last_login_at).toLocaleString() : "Never";
      const status = user.is_active ? "<span class=\"state-pill state-active\">Active</span>" : "<span class=\"state-pill state-inactive\">Inactive</span>";
      return `<tr><td><div class="product-name">${safe(user.username)}</div><div class="product-meta">${safe(user.id)}</div></td><td>${safe(user.role)}</td><td>${status}</td><td>${safe(lastLogin)}</td></tr>`;
    }).join("") : '<tr><td colspan="4" class="empty-cell">No team members found.</td></tr>';
  }

  function showPage(page) {
    if (page === "team" && state.user?.role !== "admin") page = "dashboard";
    document.querySelectorAll(".page-section").forEach((section) => { section.hidden = section.id !== `${page}-page`; });
    document.querySelectorAll(".nav-link").forEach((button) => button.classList.toggle("active", button.dataset.page === page));
    $("#page-heading").textContent = page === "dashboard" ? "Overview" : page === "inventory" ? "Inventory" : "Team";
    if (page === "team") void loadTeam();
  }

  function showApp() {
    loginScreen.hidden = true;
    appScreen.hidden = false;
    const label = state.user?.tenant_code || "Pharmacy user";
    $("#user-name").textContent = label;
    $("#user-role").textContent = state.user?.role || "Team member";
    $("#user-avatar").textContent = (label.trim()[0] || "P").toUpperCase();
    $("#team-nav").hidden = state.user?.role !== "admin";
    showPage("dashboard");
    void loadWorkspace();
  }

  async function loadWorkspace() {
    const message = $("#app-message");
    message.hidden = true;
    try {
      const since = encodeURIComponent("1970-01-01T00:00:00Z");
      const data = await api(`/api/sync/pull?since=${since}`);
      state.products = data.products || [];
      renderDashboard();
      renderInventory();
      message.hidden = false;
      message.className = "app-message success";
      message.textContent = `Inventory updated${data.server_time ? ` at ${new Date(data.server_time).toLocaleTimeString()}` : ""}.`;
      window.setTimeout(() => { message.hidden = true; }, 4000);
      if (state.user?.role === "admin") {
        try {
          const team = await api("/api/users");
          state.users = team.users || [];
          renderDashboard();
        } catch (error) {
          message.className = "app-message";
          message.textContent = `Inventory loaded; team summary unavailable: ${error.message}`;
          message.hidden = false;
        }
      }
    } catch (error) {
      message.className = "app-message";
      message.textContent = `Could not load pharmacy data: ${error.message}`;
      message.hidden = false;
    }
  }

  async function loadTeam() {
    const body = $("#team-users");
    body.innerHTML = '<tr><td colspan="4" class="empty-cell">Loading team…</td></tr>';
    try {
      const result = await api("/api/users");
      state.users = result.users || [];
      renderTeam();
      renderDashboard();
    } catch (error) {
      body.innerHTML = `<tr><td colspan="4" class="empty-cell">${safe(error.message)}</td></tr>`;
    }
  }

  $("#login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const error = $("#login-error");
    const button = $("#login-button");
    error.hidden = true;
    button.disabled = true;
    button.textContent = "Signing in…";
    const body = {
      pharmacy_code: $("#pharmacy-code").value.trim(),
      username: $("#username").value.trim(),
      password: $("#password").value
    };
    try {
      const result = await api("/api/auth/login", { method: "POST", body: JSON.stringify(body) }, false);
      state.accessToken = result.access_token;
      state.refreshToken = result.refresh_token;
      state.user = result.user;
      showApp();
    } catch (exception) {
      showError(error, exception.message);
      button.disabled = false;
      button.innerHTML = 'Sign in <span aria-hidden="true">→</span>';
    }
  });

  document.querySelectorAll(".nav-link").forEach((button) => button.addEventListener("click", () => showPage(button.dataset.page)));
  document.querySelectorAll("[data-go]").forEach((button) => button.addEventListener("click", () => showPage(button.dataset.go)));
  $("#inventory-search").addEventListener("input", renderInventory);
  $("#refresh-data").addEventListener("click", () => void loadWorkspace());
  $("#inventory-refresh").addEventListener("click", () => void loadWorkspace());
  $("#add-user").addEventListener("click", () => { $("#user-form-error").hidden = true; $("#user-dialog").showModal(); });
  $("#user-form").addEventListener("submit", async (event) => {
    if (event.submitter?.value === "cancel") return;
    event.preventDefault();
    const button = $("#create-user-button");
    const error = $("#user-form-error");
    error.hidden = true;
    button.disabled = true;
    try {
      await api("/api/users", { method: "POST", body: JSON.stringify({
        username: $("#new-username").value.trim(),
        password: $("#new-password").value,
        role: $("#new-role").value
      }) });
      $("#user-dialog").close();
      $("#user-form").reset();
      await loadTeam();
    } catch (exception) {
      showError(error, exception.message);
    } finally {
      button.disabled = false;
    }
  });
  $("#sign-out").addEventListener("click", async () => {
    try { await api("/api/auth/logout", { method: "POST", body: JSON.stringify({ refresh_token: state.refreshToken }) }); }
    catch { /* clear the local session even if the API is unreachable */ }
    clearSession();
    $("#login-error").hidden = true;
  });
})();
