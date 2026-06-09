/**
 * Registers the chart.js components used across the analytics charts.
 * Importing this module once (it is side-effecting) ensures the required
 * controllers, elements, scales, and plugins are registered before any
 * vue-chartjs <Bar>/<Line> component renders.
 */
import {
  Chart as ChartJS,
  BarController,
  LineController,
  BarElement,
  LineElement,
  PointElement,
  CategoryScale,
  LinearScale,
  Tooltip,
  Legend,
  Filler,
} from 'chart.js'

ChartJS.register(
  BarController,
  LineController,
  BarElement,
  LineElement,
  PointElement,
  CategoryScale,
  LinearScale,
  Tooltip,
  Legend,
  Filler,
)

export { ChartJS }
