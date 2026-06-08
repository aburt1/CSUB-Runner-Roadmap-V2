/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{vue,js,ts}'],
  theme: {
    extend: {
      colors: {
        csub: {
          blue: '#003594',
          'blue-dark': '#001A70',
          gold: '#FFC72C',
          'gold-light': '#F5E6BD',
          gray: '#707372',
        },
        brick: {
          DEFAULT: '#a0522d',
          dark: '#7a3b1e',
          light: '#c4956a',
        },
      },
      fontFamily: {
        display: ['"Oswald"', 'sans-serif'],
        body: ['"Open Sans"', 'sans-serif'],
      },
      animation: {
        'bounce-slow': 'bounce 2s infinite',
      },
    },
  },
  plugins: [require('@tailwindcss/typography')],
}
