# AccessCity

Accessibility-first routing engine for urban navigation. It uses a combination of OSM road graphs, PostGIS spatial data, and real-time hazard reports to calculate paths based on street safety and physical accessibility.

Project Goal: Support **SDG 11 (Sustainable Cities)** by providing safe transport for persons with disabilities (Target 11.2) and improving public safety via community hazard tracking (Target 11.7).

---

## 🏛 Architecture

AccessCity follows a modular monolithic pattern, utilizing a .NET and React Native stack with dedicated spatial infrastructure.

![Architecture Diagram](docs/images/architecture.png)

---

## 🔬 Quantitative Evaluation

We evaluated the routing engine across 10 diverse urban routes in Birmingham, measuring the sensitivity of the **Safety Score** and the resulting **Travel Cost Tradeoff**.

| Route ID | Route Name | Base Dist (km) | Safe Dist (km) | Cost Overhead | **Safety Score** |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **R01** | New St to Bullring | 0.42 | 0.42 | 0.0% | **0.94** |
| **R02** | Library to Town Hall | 0.58 | 0.58 | 0.0% | **0.92** |
| **R03** | **Aston (Hazard Zone)** | 1.40 | 1.85 | **32.1%** | **0.86** |
| **R04** | Digbeth to Southside | 1.83 | 1.83 | 0.0% | **0.91** |
| **R05** | Snow Hill to Colmore Row | 0.67 | 0.67 | 0.0% | **0.93** |
| **R06** | Jewellery Quarter to City | 1.14 | 1.14 | 0.0% | **0.89** |
| **R07** | Five Ways to Brindleyplace | 1.17 | 1.17 | 0.0% | **0.91** |
| **R08** | Edgbaston to Mailbox | 1.96 | 1.96 | 0.0% | **0.90** |
| **R09** | **Curzon (High Risk)** | 1.47 | 3.10 | **110.8%** | **0.84** |
| **R10** | **Grand Central (Const.)** | 1.34 | 1.88 | **40.3%** | **0.87** |

### 📈 Travel Cost Tradeoff Analysis
The evaluation reveals a non-linear relationship between safety and distance. While **70% of routes** require zero distance overhead to maintain high safety (Score > 0.90), the system dynamically identifies "Accessibility Deadzones" (e.g., R09, R10) where it prioritizes safety, adding up to **110% distance** to avoid severe obstacles like construction or unpaved inclines.

---

## 🧪 Ablation Study: Algorithm Sensitivity

To verify the impact of the safety heuristics, we isolated the **Hazard Proximity Factor** and measured the delta in scores for a fixed 200m segment.

| Configuration | Distance (m) | Safety Score | Delta (%) |
| :--- | :--- | :--- | :--- |
| **Control (Direct Path)** | 200m | **0.91** | -- |
| **Treatment 1 (1 Moderate Hazard)** | 200m | **0.72** | **▽ 20.8%** |
| **Treatment 2 (2 Severe Hazards)** | 200m | **0.44** | **▽ 51.6%** |
| **Active Rerouting (Safety-Aware)** | 285m | **0.85** | **△ 93.1% (vs T2)** |

**Finding**: The ablation study confirms that the model is highly sensitive to localized hazards. When safety drops below a critical threshold (e.g., T2), the routing engine overrides the "Shortest Path" heuristic, restoring the safety score via a detour (Active Rerouting).

---

## 🌍 SDG 11 Alignment

Direct technical implementation of UN targets:
- **Target 11.2 (Safe & Accessible Transport)**: Profile-specific routing constraints (Manual vs. Electric Wheelchair) ensuring safe navigation for vulnerable populations.
- **Target 11.7 (Inclusive Public Space)**: Real-time hazard reporting and risk-weighted path-finding to mitigate physical/environmental risks in public areas.

---

## 🧪 Testing

- **Unit Tests**: 45+ tests for routing cost-functions, risk math, and DTO validation.
- **Integration Tests**: 40+ tests using `WebApplicationFactory` for auth/geo flows.
- **Benchmark Suite**: Automated 10-route quantification and ablation analysis.

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
npm run web
```

---

## 🛠 Repository Layout

- `AccessCity.API`: API Layer & Logic.
- `AccessCity.Domain`: Core Entities.
- `AccessCity.Infrastructure`: PostGIS Repositories.
- `AccessCity.App`: Mobile/Web Frontend.
- `AccessCity.Tests`: XUnit & Benchmark Suite.
