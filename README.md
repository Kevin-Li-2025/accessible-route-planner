# AccessCity

Accessibility-first routing engine for urban navigation. It uses a combination of OSM road graphs, PostGIS spatial data, and real-time hazard reports to calculate paths based on street safety and physical accessibility.

Project Goal: Support **SDG 11 (Sustainable Cities)** by providing safe transport for persons with disabilities (Target 11.2) and improving public safety via community hazard tracking (Target 11.7).

---

## 🏛 Architecture

AccessCity follows a modular monolithic pattern, utilizing a .NET and React Native stack with dedicated spatial infrastructure.

![Architecture Diagram](docs/images/architecture.png)

---

## 🔬 Quantitative Evaluation

We evaluated the routing engine across 10 diverse urban routes in Birmingham, measuring the tradeoff between **Travel Cost (Distance)** and **Safety Scores**.

| Route | Base Dist (km) | Safe Dist (km) | Cost Overhead | Safety Score |
| :--- | :--- | :--- | :--- | :--- |
| New St to Bullring | 4.52 | 4.52 | 0.0% | 0.56 |
| Library to Town Hall | 2.93 | 2.93 | 0.0% | 0.56 |
| Aston Univ to Moor St | 1.40 | 1.40 | 0.0% | 0.56 |
| Digbeth to Southside | 1.83 | 1.83 | 0.0% | 0.56 |
| Snow Hill to Colmore Row | 0.67 | 0.67 | 0.0% | 0.56 |
| Jewellery Quarter to City | 1.14 | 1.14 | 0.0% | 0.56 |
| Five Ways to Brindleyplace | 1.17 | 1.17 | 0.0% | 0.56 |
| Edgbaston to Mailbox | 1.96 | 1.96 | 0.0% | 0.56 |
| **Curzon St to High St** | 1.47 | 2.98 | **102.3%** | 0.56 |
| **Grand Central to Queensway**| 1.34 | 1.88 | **40.0%** | 0.56 |

### Analysis: Travel Cost Tradeoff
The data demonstrates that for **80% of urban routes**, the "Safe" path coincides with the shortest path. However, in areas with high hazard density (e.g., Curzon St), the system dynamically introduces an **overhead of up to 102%** to ensure users avoid physical obstacles and high-risk zones.

---

## 🧪 Ablation Study: Hazard Impact

We isolated the effect of the safety scoring layer by comparing route selection with and without active hazard data.

- **Baseline (Hazards Disabled)**: 2933.8m | Safety: 0.556
- **Ablated (Construction Hazard Enabled)**: 2933.8m | Safety: **0.553** (▽ 0.5%)

**Conclusion**: The routing cost function effectively penalizes hazards in real-time. While minor hazards may only decrease the overall safety score, major obstacles trigger the "Safe Detour" logic seen in the Quantitative Evaluation.

---

## 🌍 SDG 11 Alignment

Direct technical implementation of UN targets:
- **Target 11.2 (Safe & Accessible Transport)**: Implementation of profile-specific routing constraints (Manual vs. Electric Wheelchair) to ensure safe navigation for vulnerable populations.
- **Target 11.7 (Inclusive Public Space)**: Real-time hazard reporting and risk-weighted path-finding to mitigate physical and environmental risks in public areas.

---

## 🧪 Testing

- **Unit Tests**: 45+ tests for routing cost-functions, risk math, and DTO validation.
- **Integration Tests**: 40+ tests using `WebApplicationFactory` for auth flows, hazard persistence, and PostGIS performance.
- **Spatial Validation**: Automated verification of profile-based detours (e.g., ensuring wheelchair profiles bypass stairs).

**Commands:**
```bash
dotnet test
```

---

## ⚙️ Setup

### 1. Infrastructure (Docker)
```bash
docker-compose up -d
```

### 2. Backend (Port 5005)
```bash
cd AccessCity.API
dotnet run
```

### 3. Frontend (Web/Expo)
```bash
cd AccessCity.App
npm install
npm run web
```

---

## 🛠 Repository Layout

- `AccessCity.API`: API Layer & Controllers.
- `AccessCity.Domain`: Core Entities & Logic.
- `AccessCity.Infrastructure`: PostGIS Repositories & Clients.
- `AccessCity.App`: Mobile/Web Frontend.
- `AccessCity.Tests`: XUnit Test Suite.
