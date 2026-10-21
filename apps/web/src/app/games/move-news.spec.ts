import { signedMonText, signedTilesText } from './move-news';

describe('Move news', () => {
  it('signs MON and tiles for banners and popups', () => {
    expect([signedMonText(25), signedMonText(-40), signedMonText(0)]).toEqual(['+25 MON', '−40 MON', '0 MON']);
    expect([signedTilesText(2), signedTilesText(-1), signedTilesText(1)]).toEqual(['+2 tiles', '−1 tile', '+1 tile']);
  });
});
