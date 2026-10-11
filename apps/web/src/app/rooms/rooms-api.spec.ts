import { HttpErrorResponse } from '@angular/common/http';
import { ROOM_CODE_PATTERN, normalizeRoomCode, roomErrorMessage, toRoomError } from './rooms-api';

describe('rooms API helpers', () => {
  it('reads the stable code and open room from a problem response', () => {
    const error = new HttpErrorResponse({ status: 409, error: { code: 'already_in_room', roomCode: 'K7QF3M', detail: 'x' } });

    expect(toRoomError(error)).toEqual({ code: 'already_in_room', roomCode: 'K7QF3M' });
  });

  it('treats 429 as rate limited and anything without a code as unknown', () => {
    expect(toRoomError(new HttpErrorResponse({ status: 429 })).code).toBe('rate_limited');
    expect(toRoomError(new HttpErrorResponse({ status: 500, error: 'boom' })).code).toBe('unknown');
    expect(toRoomError(new Error('offline')).code).toBe('unknown');
  });

  it('falls back to a generic message for codes it does not know', () => {
    expect(roomErrorMessage({ code: 'something_new' as never })).toBe('Something went wrong. Try again.');
    expect(roomErrorMessage({ code: 'room_full' })).toBe('This room is full.');
  });

  it.each([
    [' k7qf3m ', true],
    ['K7QF3M', true],
    ['K7QF0M', false],
    ['K7QFLM', false],
    ['K7QF3', false],
  ])('validates %s as a room code: %s', (input, valid) => {
    expect(ROOM_CODE_PATTERN.test(normalizeRoomCode(input))).toBe(valid);
  });
});
