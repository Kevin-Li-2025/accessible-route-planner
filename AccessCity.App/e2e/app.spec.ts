import { test, expect } from '@playwright/test';

test.describe('AccessCity web (Expo)', () => {
  test('landing shows AccessCity branding', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByText(/Access/i)).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(/Navigate your world/i)).toBeVisible();
  });

  test('can switch to Sign Up tab and see full name field', async ({ page }) => {
    await page.goto('/');
    await page.getByText('Sign Up', { exact: true }).click();
    await expect(page.getByPlaceholder('Full Name')).toBeVisible({ timeout: 15_000 });
  });

  test('empty sign-in shows validation', async ({ page }) => {
    await page.goto('/');
    await page.getByTestId('index-auth-submit').click();
    await expect(page.getByText(/Please fill in all mandatory fields/i)).toBeVisible({
      timeout: 10_000,
    });
  });

  test('map tab renders the web map shell', async ({ page }) => {
    await page.goto('/map');
    await expect(page.getByText(/AccessCity Map/i)).toBeVisible({ timeout: 60_000 });
  });
});
