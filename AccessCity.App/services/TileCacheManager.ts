import * as FileSystem from 'expo-file-system';
import * as SQLite from 'expo-sqlite';

/**
 * Industrial-level Tile Cache Manager for Expo.
 * Follows the "Metadata in DB, Binaries on Disk" pattern to prevent DB bloat.
 */
class TileCacheManager {
  private db: SQLite.SQLiteDatabase | null = null;
  private readonly CACHE_DIR = `${FileSystem.cacheDirectory}map_tiles/`;

  async init() {
    this.db = await SQLite.openDatabaseAsync('tiles_cache.db');
    await this.db.execAsync(`
      CREATE TABLE IF NOT EXISTS tiles (
        key TEXT PRIMARY KEY,
        path TEXT NOT NULL,
        expires_at INTEGER NOT NULL,
        etag TEXT
      );
    `);

    const dirInfo = await FileSystem.getInfoAsync(this.CACHE_DIR);
    if (!dirInfo.exists) {
      await FileSystem.makeDirectoryAsync(this.CACHE_DIR, { intermediates: true });
    }
  }

  /**
   * Generates a unique key for a tile based on XYZ coordinates.
   */
  getTileKey(x: number, y: number, z: number): string {
    return `${z}/${x}/${y}`;
  }

  /**
   * Retrieves a tile from cache or returns null if missing/expired.
   */
  async getTile(x: number, y: number, z: number): Promise<string | null> {
    if (!this.db) return null;

    const key = this.getTileKey(x, y, z);
    const result = await this.db.getFirstAsync<{ path: string; expires_at: number }>(
      'SELECT path, expires_at FROM tiles WHERE key = ?',
      [key]
    );

    if (result) {
      if (result.expires_at > Date.now()) {
        return result.path;
      } else {
        // Expired - clean up
        await this.deleteTile(key, result.path);
      }
    }
    return null;
  }

  /**
   * Saves a tile to the local filesystem and registers it in SQLite.
   */
  async saveTile(x: number, y: number, z: number, remoteUrl: string): Promise<string> {
    const key = this.getTileKey(x, y, z);
    const fileName = key.replace(/\//g, '_') + '.png';
    const localPath = `${this.CACHE_DIR}${fileName}`;

    try {
      const downloadResult = await FileSystem.downloadAsync(remoteUrl, localPath);
      
      const expiresAt = Date.now() + 1000 * 60 * 60 * 24 * 7; // 7 days cache
      await this.db?.runAsync(
        'INSERT OR REPLACE INTO tiles (key, path, expires_at) VALUES (?, ?, ?)',
        [key, downloadResult.uri, expiresAt]
      );

      return downloadResult.uri;
    } catch (error) {
      console.error('Failed to cache tile:', error);
      return remoteUrl; // Fallback to remote if cache fails
    }
  }

  private async deleteTile(key: string, path: string) {
    await this.db?.runAsync('DELETE FROM tiles WHERE key = ?', [key]);
    try {
      await FileSystem.deleteAsync(path, { Idempotent: true });
    } catch {}
  }
}

export const tileCache = new TileCacheManager();
