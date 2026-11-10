import { test, expect } from "@playwright/test";

for (const outcome of ["success", "cancel", "declined"]) {
  test(`checkout returns to the merchant after ${outcome}`, async ({
    page,
  }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/");
    await expect(
      page.getByRole("heading", { name: "Everyday, considered." }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Add to bag" }).first().click();
    await page.getByLabel("Email for your order").fill("shopper@example.com");
    await page
      .getByRole("button", { name: "Continue to secure payment" })
      .click();
    await expect(page).toHaveURL(/\/demo\/checkout\//);
    await expect(
      page.getByText("LOCAL DEMO · NOT GLOBAL PAYMENTS"),
    ).toBeVisible();
    if (outcome === "success") {
      await page
        .getByRole("button", { name: "Simulate successful payment" })
        .click();
      await expect(page).toHaveURL(/\/payment\/success\?/);
      await expect(
        page.getByRole("heading", { name: "Good things are on their way." }),
      ).toBeVisible();
      await expect(page.getByText("PAID", { exact: true })).toBeVisible();
    } else if (outcome === "cancel") {
      await page
        .getByRole("button", { name: "Cancel and return to Northstar" })
        .click();
      await expect(page).toHaveURL(/\/payment\/cancelled\?/);
      await expect(
        page.getByRole("heading", { name: "Changed your mind?" }),
      ).toBeVisible();
    } else {
      await page
        .getByRole("button", { name: "Simulate declined payment" })
        .click();
      await expect(page).toHaveURL(/\/payment\/declined\?/);
      await expect(page.getByText("DECLINED", { exact: true })).toBeVisible();
    }
    expect(errors).toEqual([]);
  });
}

test("storefront fits a mobile viewport and supports cart updates", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page.getByRole("button", { name: "Add to bag" }).first().click();
  await expect(page.getByText("1 item", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Remove one The Weekender" }).click();
  await expect(page.getByText("0 items", { exact: true })).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
