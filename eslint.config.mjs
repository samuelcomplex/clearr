import js from "@eslint/js";
import globals from "globals";

export default [
  { ignores: ["**/bin/**", "**/obj/**", "node_modules/**"] },
  js.configs.recommended,
  {
    files: ["src/**/*.js"],
    languageOptions: {
      ecmaVersion: 2020,
      sourceType: "script",
      globals: { ...globals.browser, ClearrUI: "writable", ApiClient: "readonly", Dashboard: "readonly" },
    },
    rules: {
      eqeqeq: ["error", "always", { null: "ignore" }],
      "no-implied-eval": "error",
      "no-eval": "error",
      "no-unused-vars": ["error", { args: "none" }],
    },
  },
  {
    files: ["tests/**/*.mjs"],
    languageOptions: { ecmaVersion: 2022, sourceType: "module", globals: { ...globals.node } },
  },
];
